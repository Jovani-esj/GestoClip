using System.Globalization;
using GestoClip.Core.Vision;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace GestoClip.Vision;

/// <summary>
/// Port a C# del pipeline MediaPipe Hands de OpenCV Zoo (palm detection + handpose, ONNX vía OpenCV DNN).
/// </summary>
public sealed class HandTracker : IDisposable
{
    private const int PalmSize = 192;
    private const int HandSize = 224;

    private readonly Net _palm;
    private readonly Net _hand;
    private readonly float[,] _anchors;
    private readonly string[] _palmOut;
    private readonly string[] _handOut;

    public float PalmScoreThreshold { get; init; } = 0.5f;
    public float NmsThreshold { get; init; } = 0.3f;
    public float HandConfThreshold { get; init; } = 0.8f;

    public HandTracker(string assetsDir)
    {
        _palm = CvDnn.ReadNet(Path.Combine(assetsDir, "palm_detection_mediapipe_2023feb.onnx"));
        _hand = CvDnn.ReadNet(Path.Combine(assetsDir, "handpose_estimation_mediapipe_2023feb.onnx"));
        _palmOut = _palm.GetUnconnectedOutLayersNames().Where(x => x is not null).Select(x => x!).ToArray();
        _handOut = _hand.GetUnconnectedOutLayersNames().Where(x => x is not null).Select(x => x!).ToArray();

        var lines = File.ReadAllLines(Path.Combine(assetsDir, "palm_anchors.csv")).Where(l => l.Length > 0).ToArray();
        _anchors = new float[lines.Length, 2];
        for (var i = 0; i < lines.Length; i++)
        {
            var p = lines[i].Split(',');
            _anchors[i, 0] = float.Parse(p[0], CultureInfo.InvariantCulture);
            _anchors[i, 1] = float.Parse(p[1], CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Devuelve los 21 landmarks en coordenadas de la imagen, o null si no hay mano.</summary>
    public IReadOnlyList<Landmark>? Detect(Mat bgr)
    {
        var palm = DetectBestPalm(bgr);
        return palm is null ? null : EstimatePose(bgr, palm);
    }

    // ---------------- Palm detection ----------------

    private float[]? DetectBestPalm(Mat image)
    {
        int h = image.Rows, w = image.Cols;
        var ratio = Math.Min((float)PalmSize / h, (float)PalmSize / w);
        int nh = (int)(h * ratio), nw = (int)(w * ratio);
        int padH = PalmSize - nh, padW = PalmSize - nw;
        int left = padW / 2, top = padH / 2, right = padW - left, bottom = padH - top;

        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size(nw, nh));
        using var padded = new Mat();
        Cv2.CopyMakeBorder(resized, padded, top, bottom, left, right, BorderTypes.Constant, Scalar.Black);
        using var rgb = new Mat();
        Cv2.CvtColor(padded, rgb, ColorConversionCodes.BGR2RGB);
        using var f32 = new Mat();
        rgb.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);

        var padBiasX = (int)(left / ratio);
        var padBiasY = (int)(top / ratio);

        using var blob = ToNhwc(f32, PalmSize);
        _palm.SetInput(blob);
        var outs = _palmOut.Select(_ => new Mat()).ToArray();
        try
        {
            _palm.Forward(outs, _palmOut);
            // Salidas: [0] = (1,N,18) deltas, [1] = (1,N,1) scores (según el modelo de OpenCV Zoo)
            var boxesMat = outs[0].Total() > outs[1].Total() ? outs[0] : outs[1];
            var scoreMat = ReferenceEquals(boxesMat, outs[0]) ? outs[1] : outs[0];
            var n = _anchors.GetLength(0);
            if (scoreMat.Total() != n || boxesMat.Total() != n * 18)
                throw new InvalidOperationException($"Forma inesperada del modelo de palma: {scoreMat.Total()} / {boxesMat.Total()} (anchors {n})");

            var deltas = ToFloats(boxesMat);
            var rawScores = ToFloats(scoreMat);
            var scale = Math.Max(w, h);

            var rects = new List<Rect2d>();
            var scores = new List<float>();
            var idxMap = new List<int>();
            for (var i = 0; i < n; i++)
            {
                var s = 1.0 / (1.0 + Math.Exp(-rawScores[i]));
                if (s < PalmScoreThreshold) continue;
                var cx = deltas[i * 18 + 0] / PalmSize; var cy = deltas[i * 18 + 1] / PalmSize;
                var bw = deltas[i * 18 + 2] / PalmSize; var bh = deltas[i * 18 + 3] / PalmSize;
                var x1 = (cx - bw / 2 + _anchors[i, 0]) * scale - padBiasX;
                var y1 = (cy - bh / 2 + _anchors[i, 1]) * scale - padBiasY;
                var x2 = (cx + bw / 2 + _anchors[i, 0]) * scale - padBiasX;
                var y2 = (cy + bh / 2 + _anchors[i, 1]) * scale - padBiasY;
                rects.Add(new Rect2d(x1, y1, x2 - x1, y2 - y1));
                scores.Add((float)s);
                idxMap.Add(i);
            }
            if (rects.Count == 0) return null;

            CvDnn.NMSBoxes(rects, scores, PalmScoreThreshold, NmsThreshold, out var keep);
            if (keep.Length == 0) return null;
            var best = keep.OrderByDescending(k => scores[k]).First();
            var a = idxMap[best];

            var res = new float[19];
            var r = rects[best];
            res[0] = (float)r.X; res[1] = (float)r.Y; res[2] = (float)(r.X + r.Width); res[3] = (float)(r.Y + r.Height);
            for (var k = 0; k < 7; k++)
            {
                var lx = deltas[a * 18 + 4 + k * 2] / PalmSize + _anchors[a, 0];
                var ly = deltas[a * 18 + 4 + k * 2 + 1] / PalmSize + _anchors[a, 1];
                res[4 + k * 2] = lx * scale - padBiasX;
                res[4 + k * 2 + 1] = ly * scale - padBiasY;
            }
            res[18] = scores[best];
            return res;
        }
        finally { foreach (var o in outs) o.Dispose(); }
    }

    // ---------------- Hand landmarks ----------------

    private static (Mat image, int[] bbox, int[] bias) CropAndPad(Mat image, double[] box, bool forRotation)
    {
        // box = [x1,y1,x2,y2]
        double w0 = box[2] - box[0], h0 = box[3] - box[1];
        var sx = forRotation ? 0.0 : 0.0;
        var sy = forRotation ? 0.0 : -0.4;
        double x1 = box[0] + sx * w0, y1 = box[1] + sy * h0, x2 = box[2] + sx * w0, y2 = box[3] + sy * h0;
        double cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;
        double bw = x2 - x1, bh = y2 - y1;
        var enlarge = forRotation ? 4.0 : 3.0;
        double hx = bw * enlarge / 2, hy = bh * enlarge / 2;
        var ix1 = Math.Clamp((int)(cx - hx), 0, image.Cols);
        var iy1 = Math.Clamp((int)(cy - hy), 0, image.Rows);
        var ix2 = Math.Clamp((int)(cx + hx), 0, image.Cols);
        var iy2 = Math.Clamp((int)(cy + hy), 0, image.Rows);
        if (ix2 <= ix1 || iy2 <= iy1) return (new Mat(), new[] { ix1, iy1, ix2, iy2 }, new[] { 0, 0 });

        using var crop = new Mat(image, new Rect(ix1, iy1, ix2 - ix1, iy2 - iy1));
        var side = forRotation ? (int)Math.Sqrt((double)crop.Rows * crop.Rows + (double)crop.Cols * crop.Cols) : Math.Max(crop.Rows, crop.Cols);
        var padH = side - crop.Rows; var padW = side - crop.Cols;
        int left = padW / 2, top = padH / 2, right = padW - left, bottom = padH - top;
        var padded = new Mat();
        Cv2.CopyMakeBorder(crop, padded, top, bottom, left, right, BorderTypes.Constant, Scalar.Black);
        return (padded, new[] { ix1, iy1, ix2, iy2 }, new[] { ix1 - left, iy1 - top });
    }

    private IReadOnlyList<Landmark>? EstimatePose(Mat image, float[] palm)
    {
        var (cropped, bboxI, bias) = CropAndPad(image, new double[] { palm[0], palm[1], palm[2], palm[3] }, true);
        using var _c = cropped;
        if (cropped.Empty()) return null;

        using var rgb = new Mat();
        Cv2.CvtColor(cropped, rgb, ColorConversionCodes.BGR2RGB);

        // bbox y landmarks relativos al recorte
        double bx1 = bboxI[0] - bias[0], by1 = bboxI[1] - bias[1], bx2 = bboxI[2] - bias[0], by2 = bboxI[3] - bias[1];
        var lm = new (double x, double y)[7];
        for (var i = 0; i < 7; i++) lm[i] = (palm[4 + i * 2] - bias[0], palm[4 + i * 2 + 1] - bias[1]);

        var p1 = lm[0]; var p2 = lm[2];
        var radians = Math.PI / 2 - Math.Atan2(-(p2.y - p1.y), p2.x - p1.x);
        radians -= 2 * Math.PI * Math.Floor((radians + Math.PI) / (2 * Math.PI));
        var angle = radians * 180.0 / Math.PI;

        var center = new Point2f((float)((bx1 + bx2) / 2), (float)((by1 + by2) / 2));
        using var rot = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        rot.GetArray(out double[] m); // 2x3, fila mayor
        using var rotated = new Mat();
        Cv2.WarpAffine(rgb, rotated, rot, new Size(rgb.Cols, rgb.Rows));

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in lm)
        {
            var rx = m[0] * x + m[1] * y + m[2];
            var ry = m[3] * x + m[4] * y + m[5];
            minX = Math.Min(minX, rx); maxX = Math.Max(maxX, rx);
            minY = Math.Min(minY, ry); maxY = Math.Max(maxY, ry);
        }

        var (crop2, rbox, _) = CropAndPad(rotated, new[] { minX, minY, maxX, maxY }, false);
        using var _c2 = crop2;
        if (crop2.Empty()) return null;

        using var small = new Mat();
        Cv2.Resize(crop2, small, new Size(HandSize, HandSize), 0, 0, InterpolationFlags.Area);
        using var f32 = new Mat();
        small.ConvertTo(f32, MatType.CV_32FC3, 1.0 / 255.0);
        using var blob = ToNhwc(f32, HandSize);

        _hand.SetInput(blob);
        var outs = _handOut.Select(_ => new Mat()).ToArray();
        try
        {
            _hand.Forward(outs, _handOut);
            // [0] landmarks (63), [1] conf, [2] handedness, [3] world landmarks
            var confArr = ToFloats(outs[1]);
            if (confArr[0] < HandConfThreshold) return null;
            var raw = ToFloats(outs[0]);
            if (raw.Length != 63) return null;

            double rw = rbox[2] - rbox[0], rh = rbox[3] - rbox[1];
            var s = Math.Max(rw / HandSize, rh / HandSize);

            // matriz de rotación de coordenadas (centro 0,0)
            using var cr = Cv2.GetRotationMatrix2D(new Point2f(0, 0), angle, 1.0);
            cr.GetArray(out double[] a);

            // inversa de la rotación de la imagen
            double r00 = m[0], r01 = m[3], r10 = m[1], r11 = m[4]; // [[m00,m10],[m01,m11]]
            double t0 = m[2], t1 = m[5];
            double inv0 = -(r00 * t0 + r01 * t1), inv1 = -(r10 * t0 + r11 * t1);
            double ccx = (rbox[0] + rbox[2]) / 2.0, ccy = (rbox[1] + rbox[3]) / 2.0;
            double ocx = ccx * r00 + ccy * r01 + inv0;
            double ocy = ccx * r10 + ccy * r11 + inv1;

            var result = new List<Landmark>(21);
            for (var i = 0; i < 21; i++)
            {
                var x = (raw[i * 3] - HandSize / 2.0) * s;
                var y = (raw[i * 3 + 1] - HandSize / 2.0) * s;
                var z = raw[i * 3 + 2] * s;
                var rx = x * a[0] + y * a[3];
                var ry = x * a[1] + y * a[4];
                result.Add(new Landmark((float)(rx + ocx + bias[0]), (float)(ry + ocy + bias[1]), (float)z));
            }
            return result;
        }
        finally { foreach (var o in outs) o.Dispose(); }
    }

    /// <summary>Copia un Mat float (cualquier número de dimensiones) a float[] vía puntero; GetArray no soporta Mats N-D.</summary>
    private static float[] ToFloats(Mat m)
    {
        using var c = m.IsContinuous() ? m.Reshape(1) : m.Clone().Reshape(1);
        var arr = new float[(int)m.Total() * m.Channels()];
        System.Runtime.InteropServices.Marshal.Copy(m.IsContinuous() ? m.Data : c.Data, arr, 0, arr.Length);
        return arr;
    }

    private static Mat ToNhwc(Mat f32, int size)
    {
        var len = size * size * 3;
        var src = new float[len];
        using var cont = f32.IsContinuous() ? f32.Clone() : f32.Clone(); // Clone() siempre devuelve datos contiguos
        System.Runtime.InteropServices.Marshal.Copy(cont.Data, src, 0, len);
        var blob = new Mat(new[] { 1, size, size, 3 }, MatType.CV_32FC1);
        System.Runtime.InteropServices.Marshal.Copy(src, 0, blob.Data, len);
        return blob;
    }

    public void Dispose() { _palm.Dispose(); _hand.Dispose(); }
}
