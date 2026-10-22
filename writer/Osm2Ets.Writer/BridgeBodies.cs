// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;

namespace Osm2Ets.Writer;

/// <summary>
/// Brückenkörper (Überbau samt Widerlagern) wie in der SCS-Karte: ein Railing-Modell an EINER
/// Richtungsfahrbahn eines Paars paralleler Autobahnbrücken über eine Straße, zur Gegenfahrbahn hin
/// versetzt (halber Abstand der Straßenlinien + <see cref="BridgeBodySpec.ExtraOffset"/>; in der
/// SCS-Karte 11 + 2 = 13 m). Das Modell setzt seine Widerlager an Anfang und Ende des
/// Straßenstücks – die Brücke dieser Fahrbahn wird deshalb ein einziges Stück.
/// Ob Anfangs-/Endstücke auch an Stückgrenzen innerhalb einer Brücke erschienen, ist ungeprüft;
/// gekrümmte Brücken (Punkte weiter als <see cref="MaxChordDeviation"/> neben der Sehne) bleiben
/// deshalb ohne Körper.
/// </summary>
public static class BridgeBodies
{
    public sealed record Body(RoadEntry Road, int Segment, float Offset, string Partner);

    public const float MaxChordDeviation = 1.5f;

    private sealed record Span(RoadEntry R, RunEntry Run, Vector3 A, Vector3 B)
    {
        public Vector2 A2 => new(A.X, A.Z);
        public Vector2 B2 => new(B.X, B.Z);
        public float Len => Vector2.Distance(A2, B2);
        public Vector2 Dir => Vector2.Normalize(B2 - A2);
        public Vector2 Mid => (A2 + B2) / 2;
    }

    public static List<Body> Plan(List<RoadEntry> roads, BridgeBodySpec spec, List<string> warnings)
    {
        var res = new List<Body>();
        // alle Abschnitte für die Prüfung „über eine Straße“
        var segs = new List<(RoadEntry R, Vector3 A, Vector3 B)>();
        foreach (var r in roads)
            for (int i = 0; i < r.Points.Count - 1; i++)
                segs.Add((r, MapWriter.ToVector(r.Points[i]), MapWriter.ToVector(r.Points[i + 1])));

        var spans = new List<Span>();
        foreach (var r in roads)
            foreach (var run in r.Runs.Where(x => x.Bridge && !x.Tunnel && x.Oneway &&
                         (x.Highway is "motorway" or "trunk" || x.Role == Interchanges.ExtRole)))
            {
                var a = MapWriter.ToVector(r.Points[run.From]); var b = MapWriter.ToVector(r.Points[run.To]);
                var sp = new Span(r, run, a, b);
                if (sp.Len < 5 || sp.Len > spec.MaxLength) continue;
                if (!OverRoad(r, run, segs)) continue;
                spans.Add(sp);
            }

        var used = new HashSet<Span>();
        foreach (var a in spans.OrderBy(s => s.R.Id).ThenBy(s => s.Run.From))
        {
            if (used.Contains(a)) continue;
            Span? best = null; float bestD = float.MaxValue;
            foreach (var b in spans)
            {
                if (b == a || used.Contains(b) || ReferenceEquals(b.R, a.R)) continue;
                if (Vector2.Dot(a.Dir, b.Dir) > -0.9f) continue;          // Gegenrichtung
                var right = new Vector2(-a.Dir.Y, a.Dir.X);
                float lat = Vector2.Dot(b.Mid - a.A2, right);
                if (lat >= 0) continue;                                   // Gegenfahrbahn links (Rechtsverkehr)
                float d = -lat;
                if (d < spec.MinGap || d > spec.MaxGap) continue;
                // beide Brücken beginnen und enden etwa gleich (der Körper spannt über beide und setzt
                // seine Widerlager an die Enden des Trägers)
                float s0 = Vector2.Dot(b.A2 - a.A2, a.Dir), s1 = Vector2.Dot(b.B2 - a.A2, a.Dir);
                if (MathF.Abs(MathF.Min(s0, s1)) > spec.MaxEndOffset || MathF.Abs(MathF.Max(s0, s1) - a.Len) > spec.MaxEndOffset) continue;
                if (d < bestD) { bestD = d; best = b; }
            }
            if (best is null) continue;
            used.Add(a); used.Add(best);
            // Träger: die tiefere Fahrbahn – die andere liegt dann auf dem Überbau statt darin
            var carrier = (a.A.Y + a.B.Y) <= (best.A.Y + best.B.Y) ? a : best;
            var other = ReferenceEquals(carrier, a) ? best : a;
            if (!Straight(carrier))
            {
                warnings.Add($"Info: Brücke {carrier.R.Id} gekrümmt – ohne Brückenkörper");
                continue;
            }
            int seg = Merge(carrier.R, carrier.Run);
            res.Add(new Body(carrier.R, seg, bestD / 2 + spec.ExtraOffset, other.R.Id));
        }
        return res;
    }

    /// <summary>Überquert die Brücke eine andere Straße mindestens MinClear tiefer?</summary>
    private static bool OverRoad(RoadEntry r, RunEntry run, List<(RoadEntry R, Vector3 A, Vector3 B)> segs)
    {
        for (int k = run.From; k < run.To; k++)
        {
            var p = MapWriter.ToVector(r.Points[k]); var q = MapWriter.ToVector(r.Points[k + 1]);
            foreach (var (o, a, b) in segs)
            {
                if (ReferenceEquals(o, r)) continue;
                if (!Cross(new(p.X, p.Z), new(q.X, q.Z), new(a.X, a.Z), new(b.X, b.Z), out float t, out float u)) continue;
                float yb = p.Y + (q.Y - p.Y) * t, yl = a.Y + (b.Y - a.Y) * u;
                if (yb - yl >= Underpasses.MinClear) return true;
            }
        }
        return false;
    }

    private static bool Straight(Span s)
    {
        for (int k = s.Run.From + 1; k < s.Run.To; k++)
        {
            var p = MapWriter.ToVector(s.R.Points[k]);
            var w = new Vector2(p.X, p.Z) - s.A2;
            float t = Math.Clamp(Vector2.Dot(w, s.Dir) / s.Len, 0, 1);
            var c = s.A2 + s.Dir * (t * s.Len);
            if (Vector2.Distance(new Vector2(p.X, p.Z), c) > MaxChordDeviation) return false;
            if (MathF.Abs(p.Y - (s.A.Y + (s.B.Y - s.A.Y) * t)) > 0.5f) return false;
        }
        return true;
    }

    /// <summary>Brückenlauf zu einem Abschnitt zusammenlegen; gibt dessen Index zurück.</summary>
    private static int Merge(RoadEntry r, RunEntry run)
    {
        int from = run.From, drop = run.To - run.From - 1;
        if (drop <= 0) return from;
        r.Points.RemoveRange(from + 1, drop);
        foreach (var x in r.Runs)
        {
            if (ReferenceEquals(x, run)) { x.To = from + 1; continue; }
            if (x.From > from) { x.From -= drop; x.To -= drop; }
        }
        return from;
    }

    private static bool Cross(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1, out float t, out float u)
    {
        t = u = 0;
        var r = a1 - a0; var s = b1 - b0;
        float den = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(den) < 1e-8f) return false;
        var w = b0 - a0;
        t = (w.X * s.Y - w.Y * s.X) / den;
        u = (w.X * r.Y - w.Y * r.X) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }
}
