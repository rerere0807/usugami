using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

namespace Usugami {
    static class Program {
        [STAThread] static void Main(string[] args) {
            if (args.Contains("--self-test")) { SelfTest.Run(); return; }
            bool first;
            using (var mutex = new Mutex(true, "Local\\UsugamiOverlayDesktop", out first)) {
                if (!first) { MessageBox.Show("Usugami はすでに起動しています。タスクトレイのアイコンから表示できます。", "Usugami"); return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var controller = new Controller(app);
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { controller.ShowError(e.Exception); e.Handled = true; };
                app.SessionEnding += delegate { controller.SessionEnding(); };
                // Last chance on a fatal crash: keep a recovery copy of unsaved work.
                AppDomain.CurrentDomain.UnhandledException += delegate { controller.SessionEnding(); };
                controller.Start(args.FirstOrDefault(a => Scene.IsDocument(a) && File.Exists(a))); app.Run();
            }
        }
    }

    class Mark {
        public readonly Point Position; public readonly double Pressure;
        public Mark(Point p, double pressure) { Position = p; Pressure = pressure; }
    }
    abstract class Item {
        Drawing cache;
        public abstract Item Copy();
        public abstract Rect Bounds { get; }
        protected abstract void Render(DrawingContext dc);
        public abstract void Move(Vector delta);
        public abstract void Scale(Point anchor, double factor);
        public virtual void Recolor(Color color) { }
        public virtual bool Hit(Point p, double radius) { var b = Bounds; return !b.IsEmpty && Rect.Inflate(b, radius, radius).Contains(p); }
        public void Invalidate() { cache = null; }
        // Each item keeps a frozen drawing, so redraws only rebuild what changed.
        public void Draw(DrawingContext dc) {
            if (cache == null) { var group = new DrawingGroup(); using (var c = group.Open()) Render(c); RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality); group.Freeze(); cache = group; }
            dc.DrawDrawing(cache);
        }
        protected T Clone<T>(T copy) where T : Item { ((Item)copy).cache = cache; return copy; }
        protected static Point Scaled(Point p, Point anchor, double factor) { return anchor + (p - anchor) * factor; }
        protected static Brush Fill(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        protected static Pen Line(Brush brush, double width) { var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze(); return pen; }
    }
    class StrokeItem : Item {
        public List<Mark> Points = new List<Mark>();
        public Color Color; public double Width; public bool Marker;
        public override Item Copy() { return Clone(new StrokeItem { Points = new List<Mark>(Points), Color = Color, Width = Width, Marker = Marker }); }
        public void Add(Mark mark) { Points.Add(mark); Invalidate(); }
        public override Rect Bounds { get { var r = Rect.Empty; foreach (var p in Points) r.Union(p.Position); if (!r.IsEmpty) r.Inflate(Width, Width); return r; } }
        public override void Move(Vector d) { Points = Points.Select(p => new Mark(p.Position + d, p.Pressure)).ToList(); Invalidate(); }
        public override void Scale(Point a, double f) { Points = Points.Select(p => new Mark(Scaled(p.Position, a, f), p.Pressure)).ToList(); Width *= f; Invalidate(); }
        public override void Recolor(Color color) { Color = color; Invalidate(); }
        double Thickness(double pressure) { return Marker ? Width : Width * (0.35 + 0.65 * pressure); }
        protected override void Render(DrawingContext dc) {
            if (Points.Count == 0) return;
            var brush = Fill(Color);
            if (Marker) dc.PushOpacity(0.35);
            if (Points.Count == 1) { double w = Thickness(Points[0].Pressure); dc.DrawEllipse(brush, null, Points[0].Position, w / 2, w / 2); }
            else if (Marker || Points.All(p => Math.Abs(p.Pressure - Points[0].Pressure) < 0.02)) dc.DrawGeometry(null, Line(brush, Thickness(Points[0].Pressure)), Smooth(Points.Select(p => p.Position).ToList()));
            else for (int i = 1; i < Points.Count; i++) dc.DrawLine(Line(brush, Thickness((Points[i - 1].Pressure + Points[i].Pressure) / 2)), Points[i - 1].Position, Points[i].Position);
            if (Marker) dc.Pop();
        }
        // Quadratic curves through segment midpoints remove the polyline's corners.
        public static Geometry Smooth(IList<Point> pts) {
            var g = new StreamGeometry();
            using (var ctx = g.Open()) {
                ctx.BeginFigure(pts[0], false, false);
                for (int i = 1; i < pts.Count - 1; i++) ctx.QuadraticBezierTo(pts[i], new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2), true, true);
                ctx.LineTo(pts[pts.Count - 1], true, true);
            }
            g.Freeze(); return g;
        }
        public override bool Hit(Point p, double radius) {
            if (Points.Count == 0 || !Rect.Inflate(Bounds, radius, radius).Contains(p)) return false;
            for (int i = 0; i < Points.Count; i++) {
                Point a = Points[Math.Max(0, i - 1)].Position, b = Points[i].Position;
                Vector ab = b - a;
                double t = ab.LengthSquared < 0.001 ? 0 : Math.Max(0, Math.Min(1, Vector.Multiply(p - a, ab) / ab.LengthSquared));
                if ((p - (a + ab * t)).Length <= radius + Width / 2) return true;
            }
            return false;
        }
    }
    enum ShapeKind { Line, Arrow, Rectangle, Ellipse }
    class ShapeItem : Item {
        public ShapeKind Kind; public Point Start, End; public Color Color; public double Width;
        public override Item Copy() { return Clone(new ShapeItem { Kind = Kind, Start = Start, End = End, Color = Color, Width = Width }); }
        double Head { get { return Math.Max(10, Width * 3.2); } }
        public override Rect Bounds { get { var r = new Rect(Start, End); double pad = Width / 2 + (Kind == ShapeKind.Arrow ? Head * 0.6 : 0); r.Inflate(pad, pad); return r; } }
        public override void Move(Vector d) { Start += d; End += d; Invalidate(); }
        public override void Scale(Point a, double f) { Start = Scaled(Start, a, f); End = Scaled(End, a, f); Width *= f; Invalidate(); }
        public override void Recolor(Color color) { Color = color; Invalidate(); }
        Geometry Outline() {
            if (Kind == ShapeKind.Rectangle) return new RectangleGeometry(new Rect(Start, End), Width, Width);
            if (Kind == ShapeKind.Ellipse) return new EllipseGeometry(new Rect(Start, End));
            var v = End - Start; double length = v.Length;
            if (Kind == ShapeKind.Arrow && length > 0.01) { v /= length; return new LineGeometry(Start, length > Head * 0.6 ? End - v * Head * 0.6 : Start); }
            return new LineGeometry(Start, End);
        }
        Geometry ArrowHead() {
            var v = End - Start; if (Kind != ShapeKind.Arrow || v.Length < 0.01) return null;
            v.Normalize(); var n = new Vector(-v.Y, v.X); var b = End - v * Head;
            var g = new StreamGeometry();
            using (var ctx = g.Open()) { ctx.BeginFigure(End, true, true); ctx.LineTo(b + n * Head * 0.55, true, true); ctx.LineTo(b - n * Head * 0.55, true, true); }
            g.Freeze(); return g;
        }
        protected override void Render(DrawingContext dc) {
            var brush = Fill(Color);
            dc.DrawGeometry(null, Line(brush, Width), Outline());
            var head = ArrowHead(); if (head != null) dc.DrawGeometry(brush, Line(brush, Width * 0.5), head);
        }
        public override bool Hit(Point p, double radius) {
            if (Outline().StrokeContains(new Pen(Brushes.Black, Width + 2 * radius), p)) return true;
            var head = ArrowHead(); return head != null && head.FillContains(p, radius, ToleranceType.Absolute);
        }
    }
    class TextItem : Item {
        public string Text = ""; public Point Origin; public double FontSize = 24; public Color Color;
        static readonly Typeface Face = new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        public override Item Copy() { return Clone(new TextItem { Text = Text, Origin = Origin, FontSize = FontSize, Color = Color }); }
        FormattedText Format() { return new FormattedText(Text, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, Face, FontSize, Brushes.Black, 1.0); }
        public override Rect Bounds { get { var f = Format(); return new Rect(Origin, new Size(Math.Max(f.WidthIncludingTrailingWhitespace, FontSize * 0.5), f.Height)); } }
        public override void Move(Vector d) { Origin += d; Invalidate(); }
        public override void Scale(Point a, double f) { Origin = Scaled(Origin, a, f); FontSize = Math.Max(4, Math.Min(Scene.MaxFontSize, FontSize * f)); Invalidate(); }
        public override void Recolor(Color color) { Color = color; Invalidate(); }
        protected override void Render(DrawingContext dc) {
            var geometry = Format().BuildGeometry(Origin);
            // A soft contrasting halo keeps text readable on any page underneath.
            bool light = 0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B > 150;
            var halo = Fill(light ? Color.FromArgb(190, 0, 0, 0) : Color.FromArgb(220, 255, 255, 255));
            dc.DrawGeometry(null, Line(halo, FontSize * 0.14), geometry);
            dc.DrawGeometry(Fill(Color), null, geometry);
        }
    }
    class ImageItem : Item {
        public BitmapSource Bitmap; public Rect Rectangle;
        // Size of the embedded PNG, so new images can be checked against the document limit without re-encoding everything.
        public long EncodedBytes;
        public override Rect Bounds { get { return Rectangle; } }
        public override Item Copy() { return Clone(new ImageItem { Bitmap = Bitmap, Rectangle = Rectangle, EncodedBytes = EncodedBytes }); }
        public override void Move(Vector d) { var r = Rectangle; r.Offset(d); Rectangle = r; Invalidate(); }
        public override void Scale(Point a, double f) { Rectangle = new Rect(Scaled(Rectangle.TopLeft, a, f), new Size(Rectangle.Width * f, Rectangle.Height * f)); Invalidate(); }
        protected override void Render(DrawingContext dc) { dc.DrawImage(Bitmap, Rectangle); }
    }
    static class SafeXml {
        public static XDocument Load(string path, long maxBytes, long maxCharacters, int maxElements) {
            using (var stream = File.OpenRead(path)) {
                if (stream.Length > maxBytes) throw new InvalidDataException("ファイルが大きすぎます。");
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maxCharacters, CloseInput = false };
                // Check nesting before LINQ to XML materializes attacker-controlled nodes.
                int nodes = 0, elements = 0;
                using (var reader = XmlReader.Create(stream, settings)) while (reader.Read()) {
                    if (reader.Depth > 2 || ++nodes > maxElements * 6 + 100 || reader.AttributeCount > 16 || (reader.NodeType == XmlNodeType.Element && ++elements > maxElements)) throw new InvalidDataException("XML の階層・要素・属性が多すぎます。");
                }
                stream.Position = 0;
                using (var reader = XmlReader.Create(stream, settings)) return XDocument.Load(reader);
            }
        }
        public static void Save(XDocument document, string path, long maxBytes) {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    document.Save(stream);
                    if (stream.Length > maxBytes) throw new InvalidDataException("ファイルが大きすぎます。項目を減らして保存してください。");
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    static class Pictures {
        public const int MaxSide = 20000, MaxBytes = 64 * 1024 * 1024; public const long MaxPixels = 100000000;
        static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        public static BitmapSource Decode(byte[] data, bool pngOnly) {
            return Decode(data, pngOnly, MaxPixels);
        }
        public static BitmapSource Decode(byte[] data, bool pngOnly, long pixelBudget) {
            if (data.Length > MaxBytes) throw new InvalidDataException("画像ファイルが大きすぎます（64 MiB まで）。");
            if (pngOnly && (data.Length < 8 || !PngSignature.SequenceEqual(data.Take(8)))) throw new InvalidDataException("埋め込み画像の形式が不正です。");
            using (var memory = new MemoryStream(data, false)) return Decode(memory, pixelBudget);
        }
        public static BitmapSource Load(string path) { using (var stream = File.OpenRead(path)) return Decode(stream); }
        // Read the header first so a tiny file declaring a gigantic image cannot exhaust memory.
        public static BitmapSource Decode(Stream stream) {
            return Decode(stream, MaxPixels);
        }
        public static BitmapSource Decode(Stream stream, long pixelBudget) {
            if (!stream.CanSeek || stream.Length > MaxBytes) throw new InvalidDataException("画像ファイルが大きすぎます（64 MiB まで）。");
            var probe = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (probe.Frames.Count == 0) throw new InvalidDataException("画像を読み込めません。");
            Check(probe.Frames[0].PixelWidth, probe.Frames[0].PixelHeight);
            if ((long)probe.Frames[0].PixelWidth * probe.Frames[0].PixelHeight > pixelBudget) throw new InvalidDataException("ドキュメント内の画像の合計画素数が多すぎます。");
            stream.Position = 0;
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            Check(frame.PixelWidth, frame.PixelHeight);
            if ((long)frame.PixelWidth * frame.PixelHeight > pixelBudget) throw new InvalidDataException("ドキュメント内の画像の合計画素数が多すぎます。");
            frame.Freeze(); return frame;
        }
        // Room left for one more image before the document could no longer be saved.
        // Base64 grows a PNG by a third. Strokes and text are estimated generously (a point is up to ~72 bytes of XML,
        // a character up to 6 after UTF-8 and escaping), and 5% of the file is kept as a margin.
        public static long PixelRoom(IEnumerable<Item> items) { return MaxPixels - items.OfType<ImageItem>().Sum(i => (long)i.Bitmap.PixelWidth * i.Bitmap.PixelHeight); }
        public static long ByteRoom(IEnumerable<Item> items) {
            long other = 0, images = 0;
            foreach (var item in items) {
                var stroke = item as StrokeItem; var text = item as TextItem; var image = item as ImageItem;
                if (stroke != null) other += 120 + stroke.Points.Count * 72L;
                else if (text != null) other += 200 + text.Text.Length * 6L;
                else if (image != null) { other += 200; images += (image.EncodedBytes + 2) / 3 * 4; }
                else other += 300;
            }
            return (Scene.MaxDocumentBytes * 95L / 100 - other - images) * 3 / 4;
        }
        public static long EncodedSize(BitmapSource bitmap) {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var memory = new MemoryStream()) { encoder.Save(memory); return memory.Length; }
        }
        public static BitmapSource Shrink(BitmapSource bitmap, double scale) {
            var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            // Cache the pixels so every redraw does not resample the original again.
            var cached = new CachedBitmap(scaled, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); cached.Freeze(); return cached;
        }
        public static void Check(int width, int height) {
            if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels) throw new InvalidDataException("画像が大きすぎます（一辺20,000px・合計1億画素まで）。");
        }
    }
    class Scene {
        public List<Item> Items = new List<Item>();
        public Point Offset = new Point(0, 0); public double Zoom = 1;
        readonly List<List<Item>> undo = new List<List<Item>>(), redo = new List<List<Item>>();
        public bool Dirty; bool dirtyBefore;
        List<List<Item>> redoBefore; List<Item> trimmedUndo;
        long viewRevision, viewBefore;
        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }
        List<Item> Snapshot() { return Items.Select(i => i.Copy()).ToList(); }
        public void Remember() {
            redoBefore = new List<List<Item>>(redo); trimmedUndo = null;
            undo.Add(Snapshot()); if (undo.Count > 100) { trimmedUndo = undo[0]; undo.RemoveAt(0); }
            redo.Clear(); dirtyBefore = Dirty; viewBefore = viewRevision; Dirty = true;
        }
        // Drops the snapshot of an operation that turned out to change nothing.
        public void Forget() {
            if (undo.Count == 0 || redoBefore == null) return;
            undo.RemoveAt(undo.Count - 1); if (trimmedUndo != null) undo.Insert(0, trimmedUndo);
            redo.Clear(); redo.AddRange(redoBefore); redoBefore = null; trimmedUndo = null; Dirty = dirtyBefore || (viewBefore != viewRevision && Items.Count > 0);
        }
        public void Undo(bool forward) {
            var from = forward ? redo : undo; var to = forward ? undo : redo;
            if (from.Count == 0) return;
            redoBefore = null; trimmedUndo = null;
            to.Add(Snapshot()); Items = from[from.Count - 1]; from.RemoveAt(from.Count - 1); Dirty = true;
        }
        public Point World(Point p) { return new Point((p.X - Offset.X) / Zoom, (p.Y - Offset.Y) / Zoom); }
        public Point Screen(Point p) { return new Point(p.X * Zoom + Offset.X, p.Y * Zoom + Offset.Y); }
        public void ZoomAt(Point p, double factor) {
            var world = World(p); double zoom = Math.Max(0.15, Math.Min(5, Zoom * factor));
            SetView(new Point(p.X - world.X * zoom, p.Y - world.Y * zoom), zoom);
        }
        public void SetView(Point offset, double zoom) { if (Offset == offset && Zoom == zoom) return; Offset = offset; Zoom = zoom; viewRevision++; if (Items.Count > 0) Dirty = true; }
        public void Pan(Vector delta) { SetView(Offset + delta, Zoom); }
        public const string Extension = ".usugami", LegacyExtension = ".layer";
        public const int MaxItems = 20000, MaxPoints = 500000, MaxTextLength = 10000, MaxTextCharacters = 500000;
        public const int MaxDocumentBytes = 64 * 1024 * 1024;
        public const double MaxFontSize = 4000;
        public static bool IsDocument(string path) { var ext = Path.GetExtension(path ?? "") ?? ""; return ext.Equals(Extension, StringComparison.OrdinalIgnoreCase) || ext.Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase); }
        public void ClearHistory() { undo.Clear(); redo.Clear(); redoBefore = null; trimmedUndo = null; }
        public void Save(string path) { Write(path); Dirty = false; }
        public void Write(string path) {
            CheckLimits();
            var root = new XElement("usugami", new XAttribute("version", 1), new XAttribute("x", F(Offset.X)), new XAttribute("y", F(Offset.Y)), new XAttribute("zoom", F(Zoom)));
            foreach (var item in Items) {
                var s = item as StrokeItem;
                if (s != null) root.Add(new XElement("stroke", new XAttribute("color", s.Color.ToString()), new XAttribute("width", F(s.Width)), new XAttribute("marker", s.Marker), string.Join(" ", s.Points.Select(p => F(p.Position.X) + "," + F(p.Position.Y) + "," + F(p.Pressure)))));
                var sh = item as ShapeItem;
                if (sh != null) root.Add(new XElement("shape", new XAttribute("kind", sh.Kind.ToString().ToLowerInvariant()), new XAttribute("color", sh.Color.ToString()), new XAttribute("width", F(sh.Width)), new XAttribute("x1", F(sh.Start.X)), new XAttribute("y1", F(sh.Start.Y)), new XAttribute("x2", F(sh.End.X)), new XAttribute("y2", F(sh.End.Y))));
                var t = item as TextItem;
                if (t != null) root.Add(new XElement("text", new XAttribute("color", t.Color.ToString()), new XAttribute("size", F(t.FontSize)), new XAttribute("x", F(t.Origin.X)), new XAttribute("y", F(t.Origin.Y)), t.Text));
                var im = item as ImageItem;
                if (im != null) {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(im.Bitmap));
                    using (var memory = new MemoryStream()) { encoder.Save(memory); root.Add(new XElement("image", new XAttribute("x", F(im.Rectangle.X)), new XAttribute("y", F(im.Rectangle.Y)), new XAttribute("w", F(im.Rectangle.Width)), new XAttribute("h", F(im.Rectangle.Height)), Convert.ToBase64String(memory.ToArray()))); }
                }
            }
            // Replace only after a complete write, keeping an existing document intact on failure.
            SafeXml.Save(new XDocument(root), path, MaxDocumentBytes);
        }
        public void Load(string path) {
            List<Item> next; Point offset; double zoom;
            try {
                var doc = SafeXml.Load(path, MaxDocumentBytes, MaxDocumentBytes, MaxItems + 1);
                var root = doc.Root;
                // "layer" is the root name written by earlier versions; the format is otherwise identical.
                if (root == null || (root.Name != "usugami" && root.Name != "layer") || (string)root.Attribute("version") != "1") throw new InvalidDataException("対応していないファイル形式です。");
                next = new List<Item>(); int pointCount = 0, textCharacters = 0; long imagePixels = 0;
                foreach (var el in root.Elements()) {
                    if (next.Count >= MaxItems) throw new InvalidDataException("項目数が多すぎます。");
                    if (el.HasElements) throw new InvalidDataException("描画データに不正な要素があります。");
                    if (el.Name == "stroke") {
                        var stroke = new StrokeItem { Color = ParseColor(el), Width = Positive(el, "width"), Marker = (bool)el.Attribute("marker") };
                        // Count before Split allocates a potentially huge array of points.
                        bool inPoint = false; int pointLength = 0;
                        foreach (char c in el.Value) {
                            if (c == ' ') { inPoint = false; pointLength = 0; }
                            else { if (++pointLength > 128) throw new InvalidDataException("描画の点が長すぎます。"); if (!inPoint) { inPoint = true; if (++pointCount > MaxPoints) throw new InvalidDataException("描画の点数が多すぎます。"); } }
                        }
                        foreach (var p in el.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) { var a = p.Split(new[] { ',' }, 4); if (a.Length != 3) throw new InvalidDataException("描画データが壊れています。"); double pressure = Number(a[2]); if (pressure < 0 || pressure > 1) throw new InvalidDataException("筆圧が範囲外です。"); stroke.Points.Add(new Mark(new Point(Number(a[0]), Number(a[1])), pressure)); }
                        if (stroke.Points.Count == 0) throw new InvalidDataException("描画の点がありません。");
                        next.Add(stroke);
                    } else if (el.Name == "shape") {
                        ShapeKind kind;
                        if (!Enum.TryParse((string)el.Attribute("kind"), true, out kind) || !Enum.IsDefined(typeof(ShapeKind), kind)) throw new InvalidDataException("図形の種類が不明です。");
                        next.Add(new ShapeItem { Kind = kind, Color = ParseColor(el), Width = Positive(el, "width"), Start = new Point(Number((string)el.Attribute("x1")), Number((string)el.Attribute("y1"))), End = new Point(Number((string)el.Attribute("x2")), Number((string)el.Attribute("y2"))) });
                    } else if (el.Name == "text") {
                        textCharacters += el.Value.Length;
                        if (el.Value.Length == 0 || el.Value.Length > MaxTextLength || textCharacters > MaxTextCharacters) throw new InvalidDataException("テキストが長すぎるか空です。");
                        double fontSize = Positive(el, "size"); if (fontSize > MaxFontSize) throw new InvalidDataException("文字サイズが大きすぎます。");
                        next.Add(new TextItem { Text = el.Value, Color = ParseColor(el), FontSize = fontSize, Origin = new Point(Number((string)el.Attribute("x")), Number((string)el.Attribute("y"))) });
                    } else if (el.Name == "image") {
                        // Documents only ever embed PNG; refusing other formats keeps untrusted files away from the other image decoders.
                        if (el.Value.Length > (long)Pictures.MaxBytes * 4 / 3 + 4) throw new InvalidDataException("埋め込み画像が大きすぎます。");
                        var data = Convert.FromBase64String(el.Value);
                        var bmp = Pictures.Decode(data, true, Pictures.MaxPixels - imagePixels);
                        imagePixels += (long)bmp.PixelWidth * bmp.PixelHeight;
                        next.Add(new ImageItem { Bitmap = bmp, EncodedBytes = data.Length, Rectangle = new Rect(Number((string)el.Attribute("x")), Number((string)el.Attribute("y")), Positive(el, "w"), Positive(el, "h")) });
                    } else throw new InvalidDataException("不明な描画データがあります。");
                }
                offset = new Point(Number((string)root.Attribute("x")), Number((string)root.Attribute("y")));
                zoom = Positive(root, "zoom"); if (zoom < 0.15 || zoom > 5) throw new InvalidDataException("倍率が範囲外です。");
            } catch (InvalidDataException) { throw; }
            catch (FileNotFoundException) { throw; }
            catch (Exception e) { throw new InvalidDataException("ファイルを読み込めません（" + e.Message + "）", e); }
            // A fresh history: undoing past an open would bring back the previous document under the new file name.
            ClearHistory(); Items = next; Offset = offset; Zoom = zoom; Dirty = false;
        }
        public static string F(double n) { return n.ToString("R", CultureInfo.InvariantCulture); }
        void CheckLimits() {
            if (Items.Count > MaxItems) throw new InvalidDataException("項目数が多すぎます。");
            int points = 0, characters = 0; long pixels = 0;
            CheckNumber(Offset.X); CheckNumber(Offset.Y);
            if (CheckNumber(Zoom) < 0.15 || Zoom > 5) throw new InvalidDataException("倍率が範囲外です。");
            foreach (var item in Items) {
                var s = item as StrokeItem;
                if (s != null) {
                    points += s.Points.Count; CheckPositive(s.Width);
                    if (s.Points.Count == 0 || points > MaxPoints) throw new InvalidDataException("描画の点数が範囲外です。");
                    foreach (var mark in s.Points) { CheckNumber(mark.Position.X); CheckNumber(mark.Position.Y); if (CheckNumber(mark.Pressure) < 0 || mark.Pressure > 1) throw new InvalidDataException("筆圧が範囲外です。"); }
                }
                var sh = item as ShapeItem;
                if (sh != null) { CheckPositive(sh.Width); CheckNumber(sh.Start.X); CheckNumber(sh.Start.Y); CheckNumber(sh.End.X); CheckNumber(sh.End.Y); }
                var t = item as TextItem;
                if (t != null) {
                    characters += t.Text.Length;
                    if (t.Text.Length == 0 || t.Text.Length > MaxTextLength || characters > MaxTextCharacters || CheckPositive(t.FontSize) > MaxFontSize) throw new InvalidDataException("テキストの長さまたは文字サイズが範囲外です。");
                    CheckNumber(t.Origin.X); CheckNumber(t.Origin.Y);
                }
                var image = item as ImageItem;
                if (image != null) {
                    Pictures.Check(image.Bitmap.PixelWidth, image.Bitmap.PixelHeight); pixels += (long)image.Bitmap.PixelWidth * image.Bitmap.PixelHeight;
                    if (pixels > Pictures.MaxPixels) throw new InvalidDataException("ドキュメント内の画像の合計画素数が多すぎます。");
                    CheckNumber(image.Rectangle.X); CheckNumber(image.Rectangle.Y); CheckPositive(image.Rectangle.Width); CheckPositive(image.Rectangle.Height);
                }
            }
        }
        static Color ParseColor(XElement el) { return ParseColor((string)el.Attribute("color")); }
        // Only #AARRGGBB / #RRGGBB: ColorConverter also accepts "ContextColor <uri>", which would load an ICC profile from an arbitrary location.
        public static Color ParseColor(string s) {
            if (s == null || !Regex.IsMatch(s, @"\A#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\z")) throw new InvalidDataException("色の指定が不正です。");
            uint v = uint.Parse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return s.Length == 7 ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v) : Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        static double Positive(XElement el, string name) { return CheckPositive(Number((string)el.Attribute(name))); }
        static double CheckPositive(double n) { CheckNumber(n); if (n <= 0 || n > 1000000) throw new InvalidDataException("サイズが範囲外です。"); return n; }
        static double CheckNumber(double n) { if (double.IsNaN(n) || double.IsInfinity(n) || Math.Abs(n) > 1e9) throw new InvalidDataException("座標が範囲外です。"); return n; }
        static double Number(string s) { return CheckNumber(double.Parse(s, CultureInfo.InvariantCulture)); }
    }

    enum Tool { Pen, Marker, Eraser, Line, Arrow, Rectangle, Ellipse, Text, Select, Pan }
    enum Board { None, Dim, White }
    class Surface : FrameworkElement {
        readonly Controller owner; public readonly Scene Scene;
        public Tool Tool = Tool.Pen; public Color Color = Colors.Crimson; public double PenWidth = 4; public Board Board;
        public Item Selected;
        StrokeItem stroke; ShapeItem shape; Point previous, downPos, start; Rect startBounds;
        bool dragging, panning, resizing, moved, erased; Point? hover;
        const double EraserRadius = 12;
        // A nonzero alpha makes the entire HWND receive pen/mouse input.
        static readonly Brush HitBrush = Frozen(Color.FromArgb(1, 0, 0, 0)), DimBrush = Frozen(Color.FromArgb(140, 0, 0, 0)), WhiteBrush = Frozen(Color.FromArgb(246, 250, 250, 252));
        static readonly Pen SelectPen = FrozenPen(Color.FromRgb(79, 125, 243), 1.5, true), HandlePen = FrozenPen(Color.FromRgb(79, 125, 243), 2, false), RingDark = FrozenPen(Color.FromArgb(150, 0, 0, 0), 3, false), RingLight = FrozenPen(Colors.White, 1.5, false);
        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static Pen FrozenPen(Color c, double w, bool dash) { var p = new Pen(Frozen(c), w); if (dash) p.DashStyle = new DashStyle(new double[] { 4, 3 }, 0); p.Freeze(); return p; }
        public static Brush BoardBrush(Board board) { return board == Board.Dim ? DimBrush : board == Board.White ? WhiteBrush : null; }
        public Surface(Controller owner, Scene scene) {
            this.owner = owner; Scene = scene; Focusable = true; ClipToBounds = true; Cursor = Cursors.Pen;
            MouseDown += Down; MouseMove += Move; MouseUp += Up; MouseWheel += Wheel;
            MouseLeave += delegate { if (hover != null) { hover = null; InvalidateVisual(); } };
            LostMouseCapture += delegate { Finish(); };
        }
        public bool Busy { get { return dragging || panning; } }
        protected override void OnRender(DrawingContext dc) {
            dc.DrawRectangle(BoardBrush(Board) ?? HitBrush, null, new Rect(RenderSize));
            dc.PushTransform(new TranslateTransform(Scene.Offset.X, Scene.Offset.Y)); dc.PushTransform(new ScaleTransform(Scene.Zoom, Scene.Zoom));
            foreach (var item in Scene.Items) item.Draw(dc);
            dc.Pop(); dc.Pop();
            if (Selected != null && owner.Drawing) {
                var b = Selected.Bounds; var r = new Rect(Scene.Screen(b.TopLeft), Scene.Screen(b.BottomRight)); r.Inflate(3, 3);
                dc.DrawRectangle(null, SelectPen, r);
                dc.DrawRoundedRectangle(Brushes.White, HandlePen, new Rect(r.Right - 6, r.Bottom - 6, 12, 12), 3, 3);
            }
            if (hover != null && owner.Drawing && Tool == Tool.Eraser) { dc.DrawEllipse(null, RingDark, hover.Value, EraserRadius, EraserRadius); dc.DrawEllipse(null, RingLight, hover.Value, EraserRadius, EraserRadius); }
        }
        IEnumerable<Mark> Marks(MouseEventArgs e) {
            // Promoted pen events carry every digitizer sample; use them all for smooth, pressure-aware lines.
            if (e.StylusDevice != null) {
                var points = e.StylusDevice.GetStylusPoints(this);
                bool pressure = points.Description.HasProperty(StylusPointProperties.NormalPressure);
                foreach (var sp in points) yield return new Mark(Scene.World(new Point(sp.X, sp.Y)), pressure ? sp.PressureFactor : 1);
                if (points.Count > 0) yield break;
            }
            yield return new Mark(Scene.World(e.GetPosition(this)), 1);
        }
        Item HitItem(Point p) { return Enumerable.Reverse(Scene.Items).FirstOrDefault(i => i.Hit(p, 6 / Scene.Zoom)); }
        bool OnHandle(Point pos) { if (Selected == null) return false; var b = Selected.Bounds; return (pos - Scene.Screen(b.BottomRight)).Length < 14; }
        void Down(object sender, MouseButtonEventArgs e) {
            if (!owner.Drawing || Busy) return;
            if (owner.CommitPrompt()) { e.Handled = true; return; }
            var pos = e.GetPosition(this); var p = Scene.World(pos);
            if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Tool == Tool.Pan)) {
                panning = true; previous = pos; CaptureMouse(); Cursor = Cursors.ScrollAll; e.Handled = true; return;
            }
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true; previous = downPos = pos; start = p; moved = false;
            switch (Tool) {
                case Tool.Pen: case Tool.Marker:
                    Selected = null; Scene.Remember();
                    stroke = new StrokeItem { Color = Color, Width = PenWidth * (Tool == Tool.Marker ? 4 : 1) / Scene.Zoom, Marker = Tool == Tool.Marker };
                    foreach (var m in Marks(e)) stroke.Add(m);
                    Scene.Items.Add(stroke); break;
                case Tool.Line: case Tool.Arrow: case Tool.Rectangle: case Tool.Ellipse:
                    Selected = null; Scene.Remember();
                    shape = new ShapeItem { Kind = Tool == Tool.Line ? ShapeKind.Line : Tool == Tool.Arrow ? ShapeKind.Arrow : Tool == Tool.Rectangle ? ShapeKind.Rectangle : ShapeKind.Ellipse, Start = p, End = p, Color = Color, Width = Math.Max(1.5, PenWidth) / Scene.Zoom };
                    Scene.Items.Add(shape); break;
                case Tool.Eraser: erased = false; Erase(p); break;
                case Tool.Text: {
                    var text = Scene.Items.OfType<TextItem>().LastOrDefault(t => t.Hit(p, 4 / Scene.Zoom));
                    Dispatcher.BeginInvoke(new Action(delegate { if (text != null) owner.EditText(text); else owner.CreateText(p, pos); }));
                    return;
                }
                case Tool.Select: {
                    resizing = OnHandle(pos);
                    if (!resizing) Selected = HitItem(p);
                    var text = Selected as TextItem;
                    if (text != null && e.ClickCount == 2) { Dispatcher.BeginInvoke(new Action(delegate { owner.EditText(text); })); InvalidateVisual(); return; }
                    if (Selected != null) startBounds = Selected.Bounds;
                    break;
                }
            }
            dragging = true; CaptureMouse(); InvalidateVisual(); owner.UpdateStatus();
        }
        void Move(object sender, MouseEventArgs e) {
            var pos = e.GetPosition(this);
            if (Tool == Tool.Eraser && owner.Drawing) { hover = pos; if (!dragging) InvalidateVisual(); }
            if (!dragging && !panning && owner.Drawing && Tool == Tool.Select) Cursor = OnHandle(pos) ? Cursors.SizeNWSE : Cursors.Arrow;
            if (panning) { Scene.Pan(pos - previous); previous = pos; owner.UpdateStatus(); InvalidateVisual(); return; }
            if (!dragging) return;
            var p = Scene.World(pos);
            if (stroke != null) { foreach (var m in Marks(e)) if ((m.Position - stroke.Points[stroke.Points.Count - 1].Position).Length > 0.4 / Scene.Zoom) stroke.Add(m); }
            else if (shape != null) { shape.End = Constrain(shape.Kind, start, p); shape.Invalidate(); }
            else if (Tool == Tool.Eraser) {
                // Sample along the drag so fast movements still hit thin lines in between.
                var from = Scene.World(previous); var step = p - from; int n = Math.Max(1, (int)Math.Ceiling(step.Length * Scene.Zoom / (EraserRadius / 2)));
                for (int i = 1; i <= n; i++) Erase(from + step * ((double)i / n));
            } else if (Tool == Tool.Select && Selected != null) {
                if (!moved) { if ((pos - downPos).Length < 3) return; Scene.Remember(); moved = true; }
                if (resizing) { var b = Selected.Bounds; double width = Math.Max(16 / Scene.Zoom, p.X - startBounds.Left); if (b.Width > 0.001) Selected.Scale(b.TopLeft, width / b.Width); }
                else Selected.Move((pos - previous) / Scene.Zoom);
            }
            previous = pos; InvalidateVisual();
        }
        static Point Constrain(ShapeKind kind, Point a, Point b) {
            if (!Native.IsDown(0x10)) return b;
            var v = b - a;
            if (kind == ShapeKind.Line || kind == ShapeKind.Arrow) {
                double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
                return a + new Vector(Math.Cos(angle) * v.Length, Math.Sin(angle) * v.Length);
            }
            double s = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
            return a + new Vector(s * (v.X < 0 ? -1 : 1), s * (v.Y < 0 ? -1 : 1));
        }
        void Erase(Point p) {
            double r = EraserRadius / Scene.Zoom;
            var matches = Scene.Items.Where(i => !(i is ImageItem) && i.Hit(p, r)).ToList();
            if (matches.Count == 0) return;
            if (!erased) { Scene.Remember(); erased = true; }
            foreach (var item in matches) { Scene.Items.Remove(item); if (item == Selected) Selected = null; }
        }
        void Up(object sender, MouseButtonEventArgs e) { if (!Busy) return; Finish(); ReleaseMouseCapture(); }
        public void Finish() {
            if (shape != null && (Scene.Screen(shape.End) - Scene.Screen(shape.Start)).Length < 3) { Scene.Items.Remove(shape); Scene.Forget(); }
            bool was = Busy;
            dragging = panning = resizing = moved = false; stroke = null; shape = null;
            Cursor = owner.CursorFor(Tool);
            if (was) { owner.UpdateStatus(); InvalidateVisual(); }
        }
        void Wheel(object sender, MouseWheelEventArgs e) {
            if (!owner.Drawing) return;
            e.Handled = true;
            if (owner.Follow) { owner.Toast("ページをスクロールするには Ctrl+Alt+A で操作モードへ（追従中は倍率固定）"); return; }
            if (Native.IsDown(0x11)) Scene.ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.12 : 1 / 1.12);
            else if (Native.IsDown(0x10)) Scene.Pan(new Vector(e.Delta, 0));
            else Scene.Pan(new Vector(0, e.Delta));
            owner.UpdateStatus(); InvalidateVisual();
        }
    }

    static class Theme {
        public static readonly Brush Text = Make(0xE6, 0xEA, 0xF2), Muted = Make(0x8B, 0x95, 0xA7), Accent = Make(0x4F, 0x7D, 0xF3), Teal = Make(0x0E, 0x9F, 0x7E),
            Card = Make(0x14, 0x19, 0x22), Line = Make(0x2A, 0x31, 0x40), Good = Make(0x34, 0xD3, 0x99), Warn = Make(0xF5, 0x9E, 0x0B), Bad = Make(0xF8, 0x71, 0x71), Off = Make(0x3A, 0x44, 0x57), Field = Make(0x0B, 0x0F, 0x17), Key = Make(0x22, 0x2A, 0x38);
        static Brush Make(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        public static ResourceDictionary Load() { return (ResourceDictionary)XamlReader.Parse(Xaml); }
        public static Border CardBorder(UIElement child) {
            return new Border { Child = child, Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(14),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.45, Direction = 270 } };
        }
        // Lucide-style 24×24 stroke icons.
        public static readonly Dictionary<string, string> Icons = new Dictionary<string, string> {
            { "pen", "M12 20h9 M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1l1-4z" },
            { "highlighter", "M9 11l-6 6v3h9l3-3 M22 12l-4.6 4.6a2 2 0 0 1-2.8 0l-5.2-5.2a2 2 0 0 1 0-2.8L14 4" },
            { "eraser", "M7 21l-4.3-4.3c-1-1-1-2.5 0-3.4l9.6-9.6c1-1 2.5-1 3.4 0l5.6 5.6c1 1 1 2.5 0 3.4L13 21 M22 21H7 M5 11l9 9" },
            { "line", "M5 19L19 5" },
            { "arrow", "M5 19L19 5 M9 5h10v10" },
            { "rect", "M5 4h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z" },
            { "ellipse", "M3 12a9 8 0 1 0 18 0a9 8 0 1 0-18 0z" },
            { "text", "M4 7V4h16v3 M9 20h6 M12 4v16" },
            { "pointer", "M4 4l7 17l2.5-7.5L21 11z M13.5 13.5l6 6" },
            { "hand", "M18 11V6a2 2 0 0 0-4 0 M14 10V4a2 2 0 0 0-4 0v2 M10 10.5V6a2 2 0 0 0-4 0v8 M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-0.9-6-2.3l-3.6-3.6a2 2 0 0 1 2.8-2.8L7 15" },
            { "undo", "M9 14L4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 0 11H11" },
            { "redo", "M15 14l5-5l-5-5 M20 9H9.5a5.5 5.5 0 0 0 0 11H13" },
            { "image", "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M9 11a2 2 0 1 0 0-4a2 2 0 0 0 0 4z M21 15l-3.1-3.1a2 2 0 0 0-2.8 0L6 21" },
            { "paste", "M9 2h6a1 1 0 0 1 1 1v2a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1z M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2" },
            { "trash", "M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2 M10 11v6 M14 11v6" },
            { "save", "M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z M17 21v-8H7v8 M7 3v5h8" },
            { "folder", "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.7-0.9L9.6 3.9A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z" },
            { "download", "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5l5-5 M12 15V3" },
            { "copy", "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2z M4 16a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2" },
            { "clear", "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M9 9l6 6 M15 9l-6 6" },
            { "target", "M12 2v4 M12 18v4 M2 12h4 M18 12h4 M12 17a5 5 0 1 0 0-10a5 5 0 0 0 0 10z" },
            { "eye", "M2 12C4.5 7.5 8 5 12 5s7.5 2.5 10 7c-2.5 4.5-6 7-10 7s-7.5-2.5-10-7z M12 15a3 3 0 1 0 0-6a3 3 0 0 0 0 6z" },
            { "board", "M3 4h18v12H3z M8 21l4-5l4 5" },
            { "key", "M7.5 20a4.5 4.5 0 1 0 0-9a4.5 4.5 0 0 0 0 9z M10.7 12.3L21 2 M17 6l3 3 M14.5 8.5l2 2" },
            { "help", "M12 22a10 10 0 1 0 0-20a10 10 0 0 0 0 20z M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3 M12 17h0.01" },
            { "minus", "M5 12h14" }, { "x", "M18 6L6 18 M6 6l12 12" }, { "plus", "M12 5v14 M5 12h14" },
            { "chevronUp", "M6 15l6-6l6 6" }, { "chevronDown", "M6 9l6 6l6-6" }
        };
        public static FrameworkElement Icon(string name, double size) {
            var path = new Shapes.Path { Data = Geometry.Parse(Icons[name]), StrokeThickness = 1.9, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            path.SetBinding(Shapes.Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1) });
            var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }
        public static FrameworkElement Logo(double size) {
            var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(size * 0.26), Background = new LinearGradientBrush(Color.FromRgb(99, 102, 241), Color.FromRgb(34, 211, 238), 45) });
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(new Shapes.Path { Data = Geometry.Parse("M5.8 12C9.4 6.6 14 14 18.4 8.6"), Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            canvas.Children.Add(new Shapes.Path { Data = Geometry.Parse("M5.8 16.5C9.4 11 14 18.6 18.4 13.1"), Stroke = Brushes.White, StrokeThickness = 2.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            grid.Children.Add(new Viewbox { Child = canvas });
            return grid;
        }
        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ToolTip'>
    <Setter Property='Foreground' Value='#E6EAF2'/>
    <Setter Property='FontFamily' Value='Yu Gothic UI'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ToolTip'>
      <Border Background='#0B0F17' BorderBrush='#343D4F' BorderThickness='1' CornerRadius='6' Padding='9,5'><ContentPresenter/></Border>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Btn' TargetType='Button'>
    <Setter Property='Foreground' Value='#C9D1DE'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Padding' Value='8'/>
    <Setter Property='Margin' Value='1'/>
    <Setter Property='MinWidth' Value='36'/>
    <Setter Property='MinHeight' Value='36'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='ToolTipService.InitialShowDelay' Value='300'/>
    <Setter Property='ToolTipService.ShowOnDisabled' Value='True'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
      <Border x:Name='b' Background='{TemplateBinding Background}' CornerRadius='8' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#262E3D'/><Setter Property='Foreground' Value='#FFFFFF'/></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Background' Value='#313B4E'/></Trigger>
        <Trigger Property='Tag' Value='on'><Setter TargetName='b' Property='Background' Value='#4F7DF3'/><Setter Property='Foreground' Value='#FFFFFF'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Action' TargetType='Button' BasedOn='{StaticResource Btn}'>
    <Setter Property='MinWidth' Value='54'/>
    <Setter Property='Padding' Value='6,6,6,5'/>
  </Style>
  <Style x:Key='Swatch' TargetType='Button'>
    <Setter Property='Width' Value='28'/>
    <Setter Property='Height' Value='28'/>
    <Setter Property='Margin' Value='1'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Foreground' Value='#FFFFFF'/>
    <Setter Property='ToolTipService.InitialShowDelay' Value='300'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
      <Grid Background='Transparent'>
        <Ellipse x:Name='ring' Stroke='Transparent' StrokeThickness='2'/>
        <Ellipse Margin='5' Fill='{TemplateBinding Background}' Stroke='#40FFFFFF' StrokeThickness='1'/>
        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ring' Property='Stroke' Value='#5A6478'/></Trigger>
        <Trigger Property='Tag' Value='on'><Setter TargetName='ring' Property='Stroke' Value='#FFFFFF'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Pill' TargetType='Button'>
    <Setter Property='Foreground' Value='#FFFFFF'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Margin' Value='1,1,8,1'/>
    <Setter Property='Padding' Value='12,0,14,0'/>
    <Setter Property='Height' Value='36'/>
    <Setter Property='ToolTipService.InitialShowDelay' Value='300'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
      <Border x:Name='b' Background='{TemplateBinding Background}' CornerRadius='18' Padding='{TemplateBinding Padding}'>
        <ContentPresenter VerticalAlignment='Center'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.88'/></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.75'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Plain' TargetType='Button'>
    <Setter Property='Foreground' Value='#E6EAF2'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='ToolTipService.InitialShowDelay' Value='300'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
      <Border Background='Transparent' Padding='2,4'><ContentPresenter VerticalAlignment='Center'/></Border>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='Slim' TargetType='Slider'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsMoveToPointEnabled' Value='True'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'>
      <Grid Background='Transparent' Height='24'>
        <Border Height='4' CornerRadius='2' Background='#323B4C' VerticalAlignment='Center'/>
        <Track x:Name='PART_Track'>
          <Track.DecreaseRepeatButton><RepeatButton Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Grid Background='Transparent'><Border Height='4' CornerRadius='2' Background='#4F7DF3'/></Grid></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
          <Track.IncreaseRepeatButton><RepeatButton Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
          <Track.Thumb><Thumb Focusable='False'><Thumb.Template><ControlTemplate TargetType='Thumb'><Ellipse Width='14' Height='14' Fill='#FFFFFF' Stroke='#4F7DF3' StrokeThickness='2'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
        </Track>
      </Grid>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>";
    }

    class Settings {
        public string Key; public double Left = double.NaN, Top = double.NaN, Width = 4; public Color Color = Color.FromRgb(0xF4, 0x3F, 0x5E); public bool Compact;
        // The file exists but stayed locked or denied: its key may still be the one the extension holds, so it must not be overwritten.
        public bool Unreadable;
        // An explicit override supports isolated smoke tests without touching a user's recovery files.
        static readonly string dataDirectory = Environment.GetEnvironmentVariable("USUGAMI_DATA_DIR");
        public static string Folder { get { return string.IsNullOrWhiteSpace(dataDirectory) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Usugami") : Path.GetFullPath(dataDirectory); } }
        public static bool ValidKey(string key) { return key != null && Regex.IsMatch(key, @"\A[a-f0-9]{32}\z"); }
        public static string NewKey() { var bytes = new byte[16]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return string.Concat(bytes.Select(b => b.ToString("x2"))); }
        public static Settings Load(string path) {
            // Another program (antivirus, backup, sync) can hold the file briefly; retry before giving up.
            for (int attempt = 0; ; attempt++) {
                try { return Read(path); }
                catch (Exception e) {
                    if (!(e is IOException || e is UnauthorizedAccessException)) throw;
                    if (attempt >= 4) return new Settings { Unreadable = true };
                    Thread.Sleep(250);
                }
            }
        }
        static Settings Read(string path) {
            var s = new Settings();
            try {
                if (!File.Exists(path)) return s;
                var root = SafeXml.Load(path, 100000, 100000, 1).Root;
                if (root == null || root.Name != "settings" || root.HasElements) return s;
                s.Key = (string)root.Attribute("key");
                s.Left = Read(root, "left", double.NaN); s.Top = Read(root, "top", double.NaN);
                s.Width = Math.Max(1, Math.Min(24, Read(root, "width", 4)));
                var color = (string)root.Attribute("color"); if (color != null) s.Color = Scene.ParseColor(color);
                s.Compact = (string)root.Attribute("compact") == "true";
            } catch (Exception e) { if (e is IOException || e is UnauthorizedAccessException) throw; return new Settings(); }
            return s;
        }
        static double Read(XElement e, string name, double fallback) { double n; return double.TryParse((string)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !double.IsInfinity(n) && !double.IsNaN(n) && Math.Abs(n) <= 1e9 ? n : fallback; }
        public void Save(string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            SafeXml.Save(new XDocument(new XElement("settings", new XAttribute("key", Key ?? ""), new XAttribute("left", Scene.F(Left)), new XAttribute("top", Scene.F(Top)), new XAttribute("width", Scene.F(Width)), new XAttribute("color", Color.ToString()), new XAttribute("compact", Compact ? "true" : "false"))), path, 100000);
        }
    }

    class Controller {
        readonly Application app; public readonly Scene Scene = new Scene();
        Window overlay, bar, help, prompt; Action commitPrompt; Surface surface; Settings settings; string settingsPath;
        TextBlock status, bridgeText, fileText, modeText, boardText; Shapes.Ellipse bridgeDot, widthPreview; Border followTrack; Shapes.Ellipse followKnob;
        Button mode, undoButton, redoButton, compactButton, miniButton, miniMode, customSwatch; Panel extra, toolsRow; DockPanel header; Slider widthSlider;
        readonly Dictionary<Tool, Button> toolButtons = new Dictionary<Tool, Button>();
        readonly List<KeyValuePair<Color, Button>> swatches = new List<KeyValuePair<Color, Button>>();
        public bool Drawing = false, Follow; bool visible = true, exiting, drawKeys, mini, recoveryChecked, backupFailed; int modal, ticks; string settingsWarning;
        IntPtr overlayHandle, barHandle; Forms.NotifyIcon tray; Bridge bridge;
        DispatcherTimer statusTimer; string notice = ""; DateTime noticeUntil; string file;
        readonly ScrollFollower follower = new ScrollFollower();
        string token;
        static readonly uint[] GlobalKeys = { 0x41, 0x48, 0x5A, 0x56 };
        static readonly string[] GlobalNames = { "Ctrl+Alt+A", "Ctrl+Alt+H", "Ctrl+Alt+Z", "Ctrl+Alt+V" };
        // Registered only while drawing, and released whenever a dialog needs the keyboard.
        static readonly uint[,] DrawKeys = { { 10, 0x4000, 0x1B }, { 11, 0x4002, 0x5A }, { 12, 0x4002, 0x59 }, { 13, 0x4006, 0x5A }, { 14, 0x4000, 0x2E }, { 15, 0x4002, 0x53 } };
        static readonly string[,] Palette = { { "#F43F5E", "赤" }, { "#FB923C", "オレンジ" }, { "#FACC15", "黄" }, { "#22C55E", "緑" }, { "#38BDF8", "水色" }, { "#A78BFA", "紫" }, { "#FFFFFF", "白" }, { "#111827", "黒" } };
        string RecoveryPath { get { return Path.Combine(Settings.Folder, "recovery.usugami"); } }
        public Controller(Application app) { this.app = app; }

        public void Start(string startupFile) {
            settingsPath = Path.Combine(Settings.Folder, "settings.xml");
            settings = Settings.Load(settingsPath);
            // The connection key survives restarts so the browser extension stays paired.
            if (!Settings.ValidKey(settings.Key)) { settings.Key = Settings.NewKey(); if (!settings.Unreadable) SaveSettings(); }
            if (settings.Unreadable) settingsWarning = "設定ファイルを読み込めなかったため、今回だけの接続キーを使っています。拡張機能とつながらない場合は Usugami を再起動してください";
            token = settings.Key;
            app.Resources = Theme.Load();
            overlay = new Window { Title = "Usugami Canvas", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false };
            surface = new Surface(this, Scene) { Color = settings.Color, PenWidth = settings.Width }; overlay.Content = surface;
            overlay.SourceInitialized += delegate {
                overlayHandle = new WindowInteropHelper(overlay).Handle;
                Native.SetStyle(overlayHandle, true); ResizeOverlay();
            };
            overlay.Show();
            BuildToolbar();
            bar.SourceInitialized += delegate {
                barHandle = new WindowInteropHelper(bar).Handle;
                Native.OwnBy(barHandle, overlayHandle);
                HwndSource.FromHwnd(barHandle).AddHook(WndProc);
                var failed = new List<string>();
                for (int i = 0; i < GlobalKeys.Length; i++) if (!Native.RegisterHotKey(barHandle, i + 1, 0x4003, GlobalKeys[i])) failed.Add(GlobalNames[i]);
                if (failed.Count > 0) Toast("他のアプリと競合して使えないショートカット: " + string.Join(", ", failed) + "（ツールバーのボタンは使えます）");
            };
            bar.Show(); PlaceToolbar();
            tray = new Forms.NotifyIcon { Icon = TrayIcon(), Text = "Usugami", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("ツールバーを表示", null, delegate { Later(ShowToolbar); });
            menu.Items.Add("描画 / 操作を切替  (Ctrl+Alt+A)", null, delegate { Later(ToggleDrawing); });
            menu.Items.Add("表示 / 非表示  (Ctrl+Alt+H)", null, delegate { Later(ToggleVisible); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("終了", null, delegate { Later(Exit); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Later(ShowToolbar); };
            bridge = new Bridge(token, delegate(ScrollMessage m) { app.Dispatcher.BeginInvoke(new Action(delegate { OnScroll(m); })); });
            try { bridge.Start(); } catch (Exception e) { Toast("ブラウザー連携を開始できません: " + e.Message); }
            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            statusTimer.Tick += delegate { Tick(); }; statusTimer.Start();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            UpdateTray(); UpdateStatus();
            if (settingsWarning != null) { Toast(settingsWarning); tray.ShowBalloonTip(8000, "Usugami — 設定", settingsWarning, Forms.ToolTipIcon.Warning); settingsWarning = null; }
            app.Dispatcher.BeginInvoke(new Action(delegate {
                // Ask about unsaved work first, even when started with a document; otherwise the recovery copy is silently discarded.
                if (!OfferRecovery(startupFile) && startupFile != null) Run(delegate { Scene.Load(startupFile); file = startupFile; surface.InvalidateVisual(); });
            }), DispatcherPriority.ApplicationIdle);
        }
        void Later(Action action) { app.Dispatcher.BeginInvoke(new Action(delegate { Run(action); })); }
        void DisplayChanged(object sender, EventArgs e) { app.Dispatcher.BeginInvoke(new Action(delegate { ResizeOverlay(); if (bar.IsVisible) PlaceToolbar(); })); }
        void ResizeOverlay() {
            var transform = HwndSource.FromHwnd(overlayHandle).CompositionTarget.TransformFromDevice;
            var rect = Forms.SystemInformation.VirtualScreen;
            var top = transform.Transform(new Point(rect.Left, rect.Top)); var size = transform.Transform(new Point(rect.Width, rect.Height));
            overlay.Left = top.X; overlay.Top = top.Y; overlay.Width = size.X; overlay.Height = size.Y;
        }
        double DeviceScale() { return HwndSource.FromHwnd(overlayHandle).CompositionTarget.TransformToDevice.M11; }

        // ---------- Toolbar ----------
        Style S(string key) { return (Style)app.Resources[key]; }
        Button Btn(object content, Action action, string tip, string style) {
            var b = new Button { Content = content, ToolTip = tip, Style = S(style) };
            b.Click += delegate { Run(action); }; return b;
        }
        Button Act(Panel panel, string icon, string label, Action action, string tip) {
            var stack = new StackPanel(); var ic = Theme.Icon(icon, 18); ic.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(ic);
            stack.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            var b = Btn(stack, action, tip, "Action"); panel.Children.Add(b); return b;
        }
        static StackPanel Inline(string icon, string text, out TextBlock label) {
            var s = new StackPanel { Orientation = Orientation.Horizontal }; var ic = Theme.Icon(icon, 15); ic.VerticalAlignment = VerticalAlignment.Center; s.Children.Add(ic);
            label = new TextBlock { Text = text, Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center }; s.Children.Add(label); return s;
        }
        static StackPanel Inline(string icon, string text) { TextBlock unused; return Inline(icon, text, out unused); }
        static StackPanel Row(Panel parent, double top) { var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0) }; parent.Children.Add(r); return r; }
        static void Sep(Panel p) { p.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), Background = Theme.Line, VerticalAlignment = VerticalAlignment.Center }); }
        void BuildToolbar() {
            bar = new Window { Title = "Usugami", SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = true, ShowActivated = false, Foreground = Theme.Text, FontFamily = new FontFamily("Yu Gothic UI"), FontSize = 12, UseLayoutRounding = true,
                Left = double.IsNaN(settings.Left) ? SystemParameters.WorkArea.Left + 24 : settings.Left, Top = double.IsNaN(settings.Top) ? SystemParameters.WorkArea.Top + 24 : settings.Top };
            TextOptions.SetTextFormattingMode(bar, TextFormattingMode.Display);
            var icon = AppIcon(); if (icon != null) bar.Icon = icon;
            bar.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; Later(Exit); } };
            var root = new StackPanel(); bar.Content = Theme.CardBorder(root);

            var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(2, 0, 0, 6), Cursor = Cursors.SizeAll, ToolTip = "ドラッグで移動" };
            header.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) try { bar.DragMove(); } catch (InvalidOperationException) { } };
            foreach (var b in new[] { Btn(Theme.Icon("x", 14), Exit, "Usugami を終了", "Btn"), miniButton = Btn(Theme.Icon("minus", 14), ToggleMini, "ツールバーを小さくする", "Btn"), compactButton = Btn(Theme.Icon("chevronUp", 14), ToggleCompact, "ツールバーを折りたたむ / 広げる", "Btn") }) {
                b.MinWidth = 30; b.MinHeight = 30; b.Padding = new Thickness(6); DockPanel.SetDock(b, Dock.Right); header.Children.Add(b);
            }
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(Theme.Logo(18));
            title.Children.Add(new TextBlock { Text = "Usugami", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(8, 0, 10, 1), VerticalAlignment = VerticalAlignment.Center });
            fileText = new TextBlock { Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 };
            // Shown only while minimized, so the draw/operate switch never disappears from the screen.
            miniMode = Btn(new TextBlock { FontWeight = FontWeights.SemiBold }, ToggleDrawing, "描画 / 操作を切り替え（Ctrl+Alt+A）", "Pill");
            miniMode.Height = 28; miniMode.Padding = new Thickness(12, 0, 12, 1); miniMode.Margin = new Thickness(0, 0, 6, 0); miniMode.Visibility = Visibility.Collapsed;
            title.Children.Add(fileText); title.Children.Add(miniMode); header.Children.Add(title); root.Children.Add(header);
            this.header = header;

            var tools = toolsRow = Row(root, 0);
            var pill = new StackPanel { Orientation = Orientation.Horizontal };
            pill.Children.Add(new Shapes.Ellipse { Width = 8, Height = 8, Fill = Brushes.White, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            modeText = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }; pill.Children.Add(modeText);
            pill.Children.Add(new TextBlock { Text = "Ctrl+Alt+A", FontSize = 10.5, Opacity = 0.75, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            mode = Btn(pill, ToggleDrawing, "描画モードと、下のアプリを操作するモードを切り替え", "Pill"); tools.Children.Add(mode);
            AddTool(tools, Tool.Pen, "pen", "ペン（筆圧対応）"); AddTool(tools, Tool.Marker, "highlighter", "蛍光ペン"); AddTool(tools, Tool.Eraser, "eraser", "消しゴム（なぞった線・図形・文字を消す）");
            Sep(tools);
            AddTool(tools, Tool.Line, "line", "直線（Shift で 45° 単位）"); AddTool(tools, Tool.Arrow, "arrow", "矢印（Shift で 45° 単位）"); AddTool(tools, Tool.Rectangle, "rect", "四角形（Shift で正方形）"); AddTool(tools, Tool.Ellipse, "ellipse", "楕円（Shift で正円）"); AddTool(tools, Tool.Text, "text", "テキスト（クリックした位置に入力）");
            Sep(tools);
            AddTool(tools, Tool.Select, "pointer", "選択（ドラッグで移動・右下で拡大縮小・ダブルクリックで文字編集）"); AddTool(tools, Tool.Pan, "hand", "手のひら（キャンバスを移動。中ボタンドラッグでも可）");
            Sep(tools);
            undoButton = Btn(Theme.Icon("undo", 18), delegate { Undo(false); }, "元に戻す（描画中 Ctrl+Z / いつでも Ctrl+Alt+Z）", "Btn"); tools.Children.Add(undoButton);
            redoButton = Btn(Theme.Icon("redo", 18), delegate { Undo(true); }, "やり直す（描画中 Ctrl+Y）", "Btn"); tools.Children.Add(redoButton);

            extra = new StackPanel { Visibility = settings.Compact ? Visibility.Collapsed : Visibility.Visible }; root.Children.Add(extra);
            var style = Row(extra, 6);
            for (int i = 0; i < Palette.GetLength(0); i++) {
                var color = Scene.ParseColor(Palette[i, 0]);
                var sw = new Button { Style = S("Swatch"), Background = new SolidColorBrush(color), ToolTip = Palette[i, 1] };
                sw.Click += delegate { Run(delegate { SetColor(color); }); }; swatches.Add(new KeyValuePair<Color, Button>(color, sw)); style.Children.Add(sw);
            }
            customSwatch = new Button { Style = S("Swatch"), Background = new LinearGradientBrush(Color.FromRgb(244, 63, 94), Color.FromRgb(56, 189, 248), 45), ToolTip = "ほかの色を選ぶ", Content = Theme.Icon("plus", 11) };
            customSwatch.Click += delegate { Run(PickColor); }; style.Children.Add(customSwatch);
            Sep(style);
            style.Children.Add(new TextBlock { Text = "太さ", Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 1) });
            widthSlider = new Slider { Style = S("Slim"), Minimum = 1, Maximum = 24, Value = settings.Width, Width = 112, VerticalAlignment = VerticalAlignment.Center, ToolTip = "ペン・図形の太さ（文字の大きさにも反映）" };
            widthSlider.ValueChanged += delegate { surface.PenWidth = widthSlider.Value; UpdatePreview(); }; style.Children.Add(widthSlider);
            var previewBox = new Border { Width = 32, Height = 32, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            widthPreview = new Shapes.Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; previewBox.Child = widthPreview; style.Children.Add(previewBox);
            Sep(style);
            style.Children.Add(Btn(Inline("board", "背景: なし", out boardText), CycleBoard, "背景を切り替え: なし → 暗く → ホワイトボード", "Btn"));

            var actions = Row(extra, 4);
            Act(actions, "image", "画像", ImportImage, "画像ファイルを配置");
            Act(actions, "paste", "貼り付け", Paste, "クリップボードの画像を配置（Ctrl+Alt+V）");
            Act(actions, "trash", "削除", DeleteSelected, "選択中の項目を削除（描画中 Delete）");
            Sep(actions);
            Act(actions, "save", "保存", delegate { Save(Native.IsDown(0x10)); }, "上書き保存（描画中 Ctrl+S）\nShift+クリックで名前を付けて保存");
            Act(actions, "folder", "開く", Open, ".usugami ファイルを開く");
            Act(actions, "download", "PNG", Export, "書き込み全体を透明PNGで書き出す");
            Act(actions, "copy", "コピー", CopyImage, "書き込み全体を画像としてクリップボードへ");
            Sep(actions);
            Act(actions, "clear", "全消去", Clear, "すべて消去（元に戻せます）");
            Act(actions, "target", "位置リセット", ResetView, "キャンバスの位置・倍率を初期状態へ");
            Act(actions, "eye", "非表示", ToggleVisible, "書き込みを一時的に隠す（Ctrl+Alt+H）");

            extra.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(2, 8, 2, 6) });
            var connect = new DockPanel(); extra.Children.Add(connect);
            var help = Btn(Inline("help", "使い方"), ShowHelp, "操作方法・ショートカット・ブラウザー連携の設定", "Btn"); DockPanel.SetDock(help, Dock.Right); connect.Children.Add(help);
            var copyKey = Btn(Inline("key", "接続キーをコピー"), CopyKey, "ブラウザー拡張の設定に貼り付けるキー（再起動しても変わりません）", "Btn"); DockPanel.SetDock(copyKey, Dock.Right); connect.Children.Add(copyKey);
            var sw2 = new StackPanel { Orientation = Orientation.Horizontal };
            followTrack = new Border { Width = 34, Height = 20, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center };
            followKnob = new Shapes.Ellipse { Width = 14, Height = 14, Fill = Brushes.White, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center }; followTrack.Child = followKnob;
            sw2.Children.Add(followTrack); sw2.Children.Add(new TextBlock { Text = "ページ追従", Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
            connect.Children.Add(Btn(sw2, ToggleFollow, "Edge / Chrome 拡張と連携し、ページのスクロールに書き込みを追従させる", "Plain"));
            bridgeDot = new Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }; connect.Children.Add(bridgeDot);
            bridgeText = new TextBlock { Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center }; connect.Children.Add(bridgeText);

            status = new TextBlock { FontSize = 11.5, Foreground = Theme.Muted, Margin = new Thickness(4, 6, 4, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
            root.Children.Add(status);
            SelectTool(Tool.Pen, false); UpdatePreview(); UpdateSwatches();
        }
        void AddTool(Panel panel, Tool tool, string icon, string tip) { var b = Btn(Theme.Icon(icon, 18), delegate { SelectTool(tool); }, tip, "Btn"); toolButtons.Add(tool, b); panel.Children.Add(b); }
        public Cursor CursorFor(Tool tool) {
            switch (tool) {
                case Tool.Pan: return Cursors.ScrollAll;
                case Tool.Select: return Cursors.Arrow;
                case Tool.Text: return Cursors.IBeam;
                case Tool.Pen: case Tool.Marker: return Cursors.Pen;
                default: return Cursors.Cross;
            }
        }
        void SelectTool(Tool tool) { SelectTool(tool, true); }
        void SelectTool(Tool tool, bool activate) {
            surface.Tool = tool; surface.Selected = null; surface.Cursor = CursorFor(tool);
            // Picking a tool means the user wants to draw now.
            if (activate && !Drawing) ToggleDrawing();
            surface.InvalidateVisual(); UpdateStatus();
        }
        void SetColor(Color color) {
            surface.Color = color;
            var selected = surface.Selected;
            if (selected != null && !(selected is ImageItem)) { Scene.Remember(); selected.Recolor(color); surface.InvalidateVisual(); Toast("選択中の項目の色を変更しました"); }
            else if (surface.Tool == Tool.Eraser || surface.Tool == Tool.Select || surface.Tool == Tool.Pan) SelectTool(Tool.Pen);
            else if (!Drawing) ToggleDrawing();
            UpdateSwatches(); UpdatePreview();
        }
        void PickColor() {
            var c = surface.Color;
            using (var dialog = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B) }) {
                if (Modal(() => dialog.ShowDialog(new Owner32(barHandle))) != Forms.DialogResult.OK) return;
                SetColor(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
            }
        }
        void UpdateSwatches() {
            bool matched = false;
            foreach (var pair in swatches) { bool on = pair.Key == surface.Color; pair.Value.Tag = on ? "on" : null; matched |= on; }
            customSwatch.Tag = matched ? null : "on";
            if (!matched) customSwatch.Background = new SolidColorBrush(surface.Color);
        }
        void UpdatePreview() {
            if (widthPreview == null) return;
            double d = Math.Max(2, Math.Min(26, surface.PenWidth)); widthPreview.Width = d; widthPreview.Height = d;
            widthPreview.Fill = new SolidColorBrush(surface.Color); widthPreview.Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)); widthPreview.StrokeThickness = 1;
        }
        void CycleBoard() {
            surface.Board = (Board)(((int)surface.Board + 1) % 3);
            if (surface.Board != Board.None && !visible) SetVisible(true);
            surface.InvalidateVisual();
            Toast(surface.Board == Board.None ? "背景をなしに戻しました" : surface.Board == Board.Dim ? "背景を暗くしました。書き込みが目立ちます" : "ホワイトボードにしました。下の画面は隠れます");
        }
        void ToggleCompact() {
            settings.Compact = !settings.Compact;
            UpdateLayout();
            compactButton.Content = Theme.Icon(settings.Compact ? "chevronDown" : "chevronUp", 14);
        }
        void ToggleMini() { mini = !mini; UpdateLayout(); }
        void UpdateLayout() {
            var collapsed = mini ? Visibility.Collapsed : Visibility.Visible;
            toolsRow.Visibility = status.Visibility = fileText.Visibility = compactButton.Visibility = collapsed;
            extra.Visibility = mini || settings.Compact ? Visibility.Collapsed : Visibility.Visible;
            miniMode.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
            header.Margin = new Thickness(2, 0, 0, mini ? 0 : 6);
            miniButton.Content = Theme.Icon(mini ? "chevronDown" : "minus", 14);
            miniButton.ToolTip = mini ? "ツールバーを元の大きさに戻す" : "ツールバーを小さくする";
            UpdateStatus();
        }
        void PlaceToolbar() {
            // Keep the header on a connected monitor, e.g. after a display was unplugged.
            var source = PresentationSource.FromVisual(bar); if (source == null) return;
            var corner = source.CompositionTarget.TransformToDevice.Transform(new Point(bar.Left + 60, bar.Top + 30));
            if (!Forms.Screen.AllScreens.Any(s => s.WorkingArea.Contains((int)corner.X, (int)corner.Y))) { bar.Left = SystemParameters.WorkArea.Left + 24; bar.Top = SystemParameters.WorkArea.Top + 24; }
        }
        void ShowToolbar() { bar.Show(); PlaceToolbar(); Native.Raise(barHandle); }
        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) {
            // Keep keyboard focus in the browser when clicking drawing tools.
            if (msg == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
            if (msg == 0x0312) { int id = w.ToInt32(); app.Dispatcher.BeginInvoke(new Action(delegate { Run(delegate { Hotkey(id); }); })); handled = true; }
            return IntPtr.Zero;
        }
        void Hotkey(int id) {
            switch (id) {
                case 1: ToggleDrawing(); break;
                case 2: ToggleVisible(); break;
                case 3: case 11: Undo(false); break;
                case 4: Paste(); break;
                // While drawing, Esc is a global hotkey and never reaches the help window's own key handler.
                case 10: if (help != null && help.IsActive) help.Close(); else if (surface.Selected != null) { surface.Selected = null; surface.InvalidateVisual(); } else if (Drawing) ToggleDrawing(); break;
                case 12: case 13: Undo(true); break;
                case 14: DeleteSelected(); break;
                case 15: Save(false); break;
            }
        }
        void UpdateKeys() {
            bool want = Drawing && visible && modal == 0 && !exiting;
            if (want == drawKeys || barHandle == IntPtr.Zero) return; drawKeys = want;
            for (int i = 0; i < DrawKeys.GetLength(0); i++) {
                if (want) Native.RegisterHotKey(barHandle, (int)DrawKeys[i, 0], DrawKeys[i, 1], DrawKeys[i, 2]);
                else Native.UnregisterHotKey(barHandle, (int)DrawKeys[i, 0]);
            }
        }
        T Modal<T>(Func<T> show) {
            if (bar != null && !bar.IsVisible) ShowToolbar();
            modal++; UpdateKeys();
            try { return show(); } finally { modal--; UpdateKeys(); }
        }
        void Run(Action action) {
            try { CommitPrompt(); surface.Finish(); surface.ReleaseMouseCapture(); action(); }
            catch (Exception e) { ShowError(e); }
            UpdateStatus();
        }
        public void ShowError(Exception e) {
            if (bar == null) { MessageBox.Show(e.Message, "Usugami — エラー", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            Modal(() => MessageBox.Show(bar, e.Message, "Usugami — エラー", MessageBoxButton.OK, MessageBoxImage.Warning));
        }

        // ---------- Modes ----------
        public void ToggleDrawing() {
            surface.Finish(); Drawing = !Drawing;
            if (Drawing && !visible) SetVisible(true);
            Native.SetStyle(overlayHandle, !Drawing); surface.Selected = null; surface.InvalidateVisual();
            Native.Raise(barHandle); UpdateKeys(); UpdateTray(); UpdateStatus();
        }
        void ToggleVisible() { SetVisible(!visible); }
        void SetVisible(bool show) {
            visible = show;
            if (visible) overlay.Show(); else { overlay.Hide(); if (Drawing) ToggleDrawing(); }
            Native.Raise(barHandle); UpdateKeys(); UpdateStatus();
            if (!show) Toast("書き込みを隠しました。Ctrl+Alt+H で再表示します");
        }
        void UpdateTray() { if (tray != null) tray.Text = Drawing ? "Usugami — 描画中（Ctrl+Alt+A で操作へ）" : "Usugami — 操作中（Ctrl+Alt+A で描画へ）"; }
        public void Toast(string text) { notice = text; noticeUntil = DateTime.UtcNow.AddSeconds(6); UpdateStatus(); }
        void Tick() {
            UpdateStatus();
            // Crash/power-loss protection: keep a recovery copy of unsaved work.
            // The timer keeps running under the restore question; the copy must survive until it is answered.
            if (++ticks % 60 == 0 && recoveryChecked) {
                try {
                    if (Scene.Dirty && Scene.Items.Count > 0) { Directory.CreateDirectory(Settings.Folder); Scene.Write(RecoveryPath); } else DeleteRecovery();
                    backupFailed = false;
                } catch (Exception e) {
                    // Warn once per failure streak: without a backup, a crash would lose the unsaved work.
                    if (!backupFailed) {
                        backupFailed = true;
                        string text = "自動バックアップに失敗しました（" + e.Message.Trim() + "）。こまめに保存してください";
                        Toast(text);
                        if (tray != null) tray.ShowBalloonTip(8000, "Usugami — 自動バックアップ", text, Forms.ToolTipIcon.Warning);
                    }
                }
            }
        }
        public void UpdateStatus() {
            if (status == null) return;
            modeText.Text = Drawing ? "描画中" : "操作中";
            mode.Background = miniMode.Background = Drawing ? Theme.Accent : Theme.Teal;
            ((TextBlock)miniMode.Content).Text = (Drawing ? "● 描画中" : "● 操作中");
            foreach (var pair in toolButtons) pair.Value.Tag = pair.Key == surface.Tool ? "on" : null;
            undoButton.IsEnabled = Scene.CanUndo; redoButton.IsEnabled = Scene.CanRedo;
            followTrack.Background = Follow ? Theme.Good : Theme.Off; followKnob.HorizontalAlignment = Follow ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            bool running = bridge != null && bridge.Running, seen = running && DateTime.UtcNow - bridge.LastSeen < TimeSpan.FromSeconds(4);
            bool badKey = running && !seen && DateTime.UtcNow - bridge.LastRejected < TimeSpan.FromSeconds(4);
            bridgeDot.Fill = !running || badKey ? Theme.Bad : seen ? Theme.Good : Theme.Warn;
            bridgeText.Text = !running ? "連携停止" : seen ? "ブラウザー接続済み" : badKey ? "接続キーが不一致" : "拡張の接続待ち";
            bridgeText.ToolTip = badKey ? "拡張機能のキーが古いか違います。「接続キーをコピー」して拡張の設定に貼り直してください" : null;
            fileText.Text = (file == null ? "新規レイヤー" : Path.GetFileName(file)) + (Scene.Dirty ? "  •  未保存" : "");
            boardText.Text = surface.Board == Board.None ? "背景: なし" : surface.Board == Board.Dim ? "背景: 暗く" : "背景: ホワイト";
            status.Text = DateTime.UtcNow < noticeUntil ? notice : Hint();
            status.Foreground = DateTime.UtcNow < noticeUntil ? Theme.Text : Theme.Muted;
        }
        string Hint() {
            if (!visible) return "レイヤー非表示中  ·  Ctrl+Alt+H で再表示";
            if (!Drawing) return "下のアプリを操作できます  ·  Ctrl+Alt+A またはツールを選ぶと描画に戻ります";
            string tip;
            switch (surface.Tool) {
                case Tool.Pen: tip = "ドラッグで描画"; break;
                case Tool.Marker: tip = "なぞって強調"; break;
                case Tool.Eraser: tip = "なぞった線・図形・文字を消去"; break;
                case Tool.Line: case Tool.Arrow: tip = "ドラッグで描画 · Shift で角度固定"; break;
                case Tool.Rectangle: case Tool.Ellipse: tip = "ドラッグで描画 · Shift で縦横比固定"; break;
                case Tool.Text: tip = "クリックで文字を追加 · 文字をクリックで編集"; break;
                case Tool.Select: tip = "ドラッグで移動 · 右下で拡大縮小 · 色をクリックで色変更"; break;
                default: tip = "ドラッグでキャンバスを移動"; break;
            }
            return string.Format("{0}  ·  {1:0}%  ·  {2} 件  ·  Esc で操作モード", tip, Scene.Zoom * 100, Scene.Items.Count);
        }

        // ---------- Editing ----------
        void Undo(bool forward) { surface.Finish(); Scene.Undo(forward); surface.Selected = null; surface.InvalidateVisual(); UpdateStatus(); }
        void DeleteSelected() {
            if (surface.Selected == null) { Toast("「選択」ツールで項目をクリックしてから削除してください"); return; }
            Scene.Remember(); Scene.Items.Remove(surface.Selected); surface.Selected = null; surface.InvalidateVisual();
        }
        void Clear() {
            if (Scene.Items.Count == 0) return;
            Scene.Remember(); Scene.Items.Clear(); surface.Selected = null; surface.InvalidateVisual(); Toast("すべて消去しました（元に戻せます）");
        }
        void ResetView() { Scene.SetView(new Point(), Follow ? Scene.Zoom : 1); follower.Reset(); surface.InvalidateVisual(); }
        public void CreateText(Point world, Point at) {
            OpenPrompt("", at, delegate(string text) {
                Scene.Remember();
                var item = new TextItem { Text = text, Origin = world, Color = surface.Color, FontSize = (12 + surface.PenWidth * 2.5) / Scene.Zoom };
                Scene.Items.Add(item); surface.Selected = null; surface.InvalidateVisual();
            });
        }
        public void EditText(TextItem item) {
            OpenPrompt(item.Text, Scene.Screen(item.Origin), delegate(string text) {
                if (text == item.Text) return;
                Scene.Remember();
                if (text.Length == 0) { Scene.Items.Remove(item); if (surface.Selected == item) surface.Selected = null; }
                else { item.Text = text; item.Invalidate(); }
                surface.InvalidateVisual();
            }, true);
        }
        void OpenPrompt(string initial, Point at, Action<string> done) { OpenPrompt(initial, at, done, false); }
        void OpenPrompt(string initial, Point at, Action<string> done, bool allowEmpty) {
            CommitPrompt();
            var screen = HwndSource.FromHwnd(overlayHandle).CompositionTarget.TransformFromDevice.Transform(surface.PointToScreen(at));
            var box = new TextBox { Text = initial, MaxLength = Scene.MaxTextLength, MinWidth = 280, MaxWidth = 560, FontSize = 16, Background = Theme.Field, Foreground = Brushes.White, CaretBrush = Brushes.White, SelectionBrush = Theme.Accent, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, FontFamily = new FontFamily("Yu Gothic UI") };
            var stack = new StackPanel();
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            top.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(surface.Color), BorderBrush = Theme.Off, BorderThickness = new Thickness(1), Margin = new Thickness(2, 0, 8, 0) });
            top.Children.Add(new TextBlock { Text = initial.Length > 0 ? "テキストを編集（空にすると削除）" : "テキストを追加", Foreground = Theme.Text, FontSize = 12 });
            stack.Children.Add(top); stack.Children.Add(box);
            stack.Children.Add(new TextBlock { Text = "Enter で確定 · Shift+Enter で改行 · Esc で取消", Foreground = Theme.Muted, FontSize = 11, Margin = new Thickness(2, 6, 0, 0) });
            var card = Theme.CardBorder(stack); card.Padding = new Thickness(10);
            var win = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, Content = card, FontFamily = new FontFamily("Yu Gothic UI"), Left = screen.X - 26, Top = screen.Y - 50 };
            bool finished = false;
            Action<bool> finish = delegate(bool accept) {
                if (finished) return; finished = true; prompt = null; commitPrompt = null; modal--; UpdateKeys();
                app.Dispatcher.BeginInvoke(new Action(win.Close));
                var text = Regex.Replace(box.Text, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "").TrimEnd();
                if (accept && (text.Length > 0 || allowEmpty)) { try { done(text); } catch (Exception e) { ShowError(e); } }
                UpdateStatus();
            };
            commitPrompt = delegate { finish(true); };
            box.PreviewKeyDown += delegate(object s, KeyEventArgs e) {
                if (e.Key == Key.Escape) { finish(false); e.Handled = true; }
                else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { finish(true); e.Handled = true; }
            };
            win.Deactivated += delegate { finish(true); };
            win.Loaded += delegate {
                var area = SystemParameters.VirtualScreenWidth;
                win.Left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(win.Left, SystemParameters.VirtualScreenLeft + area - win.ActualWidth));
                win.Activate(); box.Focus(); Keyboard.Focus(box); box.SelectAll();
            };
            if (!bar.IsVisible) ShowToolbar();
            modal++; UpdateKeys(); prompt = win; win.Owner = bar; win.Show();
        }
        public bool CommitPrompt() { if (prompt == null) return false; commitPrompt(); return true; }

        // ---------- Images ----------
        void AddImage(BitmapSource bitmap, int index) {
            Pictures.Check(bitmap.PixelWidth, bitmap.PixelHeight);
            if (bitmap.CanFreeze && !bitmap.IsFrozen) bitmap.Freeze();
            // Place on the monitor under the cursor, at its real pixel size when it fits.
            var monitor = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
            var area = new Rect(surface.PointFromScreen(new Point(monitor.Left, monitor.Top)), surface.PointFromScreen(new Point(monitor.Right, monitor.Bottom)));
            double width = Math.Min(bitmap.PixelWidth / DeviceScale(), area.Width * 0.6), height = width * bitmap.PixelHeight / bitmap.PixelWidth;
            if (height > area.Height * 0.7) { height = area.Height * 0.7; width = height * bitmap.PixelWidth / bitmap.PixelHeight; }
            var center = Scene.World(new Point(area.X + area.Width / 2 + index * 28, area.Y + area.Height / 2 + index * 28));
            width /= Scene.Zoom; height /= Scene.Zoom;
            long encoded; bitmap = FitImage(bitmap, out encoded); if (bitmap == null) return;
            Scene.Remember();
            var image = new ImageItem { Bitmap = bitmap, EncodedBytes = encoded, Rectangle = new Rect(center.X - width / 2, center.Y - height / 2, width, height) };
            Scene.Items.Add(image);
            SelectTool(Tool.Select); surface.Selected = image; surface.InvalidateVisual();
            Toast("画像をドラッグで移動、右下のハンドルで拡大縮小できます");
        }
        // Checked when the image is added: limits found only at save time would leave unsavable work with no backup.
        BitmapSource FitImage(BitmapSource bitmap, out long encoded) {
            long pixelRoom = Pictures.PixelRoom(Scene.Items), byteRoom = Pictures.ByteRoom(Scene.Items);
            long pixels = (long)bitmap.PixelWidth * bitmap.PixelHeight;
            encoded = Pictures.EncodedSize(bitmap);
            if (pixels <= pixelRoom && encoded <= byteRoom) return bitmap;
            const string limits = "（画像の合計は 1 億画素、ファイルは 64 MiB まで）";
            if (pixelRoom < 10000 || byteRoom < 100000) throw new InvalidDataException("これ以上画像を追加すると保存できなくなります" + limits + "。不要な画像を削除してください。");
            double scale = Math.Min(Math.Sqrt((double)pixelRoom / pixels), Math.Sqrt((double)byteRoom / encoded)) * 0.95;
            string question = string.Format("この画像をそのまま追加すると、ドキュメントの上限{0}を超えて保存できなくなります。\n\n縮小して追加しますか？（{1}×{2} → 約 {3}×{4}）",
                limits, bitmap.PixelWidth, bitmap.PixelHeight, Math.Max(1, (int)(bitmap.PixelWidth * scale)), Math.Max(1, (int)(bitmap.PixelHeight * scale)));
            if (Modal(() => MessageBox.Show(bar, question, "Usugami", MessageBoxButton.YesNo, MessageBoxImage.Question)) != MessageBoxResult.Yes) { Toast("画像の追加を取りやめました"); return null; }
            // PNG size does not shrink exactly with the pixel count, so verify and step down if needed.
            for (int attempt = 0; attempt < 5; attempt++, scale *= 0.8) {
                var smaller = Pictures.Shrink(bitmap, scale);
                encoded = Pictures.EncodedSize(smaller);
                if ((long)smaller.PixelWidth * smaller.PixelHeight <= pixelRoom && encoded <= byteRoom) return smaller;
            }
            throw new InvalidDataException("画像を十分に縮小できませんでした" + limits + "。不要な画像を削除してください。");
        }
        static bool IsImageFile(string path) { return Regex.IsMatch(Path.GetExtension(path) ?? "", @"^\.(png|jpe?g|bmp|gif|tiff?|ico|webp)$", RegexOptions.IgnoreCase); }
        // Clipboard DIBs from many screenshot tools have an all-zero alpha channel; treat those as opaque.
        static BitmapSource FixAlpha(BitmapSource source) {
            Pictures.Check(source.PixelWidth, source.PixelHeight);
            if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32) return source;
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4; var pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) return source;
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        }
        void ImportImage() {
            var dialog = new OpenFileDialog { Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico|すべてのファイル|*.*", Multiselect = true };
            if (Modal(() => dialog.ShowDialog(bar)) != true) return;
            for (int i = 0; i < dialog.FileNames.Length; i++) AddImage(Pictures.Load(dialog.FileNames[i]), i);
        }
        void Paste() {
            var png = WithClipboard(() => Clipboard.ContainsData("PNG") ? Clipboard.GetData("PNG") as MemoryStream : null);
            if (png != null) { if (png.Length > Pictures.MaxBytes) throw new InvalidDataException("画像ファイルが大きすぎます（64 MiB まで）。"); AddImage(Pictures.Decode(png.ToArray(), true), 0); return; }
            var image = WithClipboard(() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null);
            if (image != null) { AddImage(FixAlpha(image), 0); return; }
            var dropped = WithClipboard(() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : null);
            if (dropped != null) {
                var files = dropped.Where(IsImageFile).ToList();
                for (int i = 0; i < files.Count; i++) AddImage(Pictures.Load(files[i]), i);
                if (files.Count > 0) return;
            }
            Toast("クリップボードに画像がありません");
        }
        // Another app holding the clipboard open makes these calls fail briefly; retry before reporting.
        static T WithClipboard<T>(Func<T> action) {
            for (int attempt = 0; ; attempt++) {
                try { return action(); }
                catch (ExternalException) { if (attempt >= 5) throw new IOException("クリップボードを使用できません。少し待ってからもう一度お試しください。"); Thread.Sleep(60); }
            }
        }
        static void CopyText(string text) { WithClipboard(() => { Clipboard.SetDataObject(text, true); return true; }); }

        // ---------- Files ----------
        bool SaveIfNeeded() {
            if (!Scene.Dirty || (Scene.Items.Count == 0 && file == null)) return true;
            var result = Modal(() => MessageBox.Show(bar, "書き込みの変更を保存しますか？", "Usugami", MessageBoxButton.YesNoCancel, MessageBoxImage.Question));
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes) return Save(false);
            return true;
        }
        bool Save(bool saveAs) {
            string path = file;
            if (path == null || saveAs) {
                var dialog = new SaveFileDialog { Filter = "Usugami ドキュメント|*.usugami", DefaultExt = Scene.Extension, FileName = file == null ? "解説.usugami" : Path.GetFileNameWithoutExtension(file) + Scene.Extension };
                if (Modal(() => dialog.ShowDialog(bar)) != true) return false;
                path = dialog.FileName;
            }
            Scene.Save(path); file = path; DeleteRecovery(); Toast("保存しました: " + Path.GetFileName(path)); return true;
        }
        void Open() {
            if (!SaveIfNeeded()) return;
            var dialog = new OpenFileDialog { Filter = "Usugami ドキュメント|*.usugami;*.layer" };
            if (Modal(() => dialog.ShowDialog(bar)) != true) return;
            Scene.Load(dialog.FileName); file = dialog.FileName; surface.Selected = null; follower.Reset(); surface.InvalidateVisual(); DeleteRecovery();
            Toast("読み込みました。ページ追従の基準位置をリセットしました");
        }
        // Returns true when the previous session's work was restored.
        bool OfferRecovery(string startupFile) {
            try {
                if (!File.Exists(RecoveryPath)) return false;
                string question = "前回、保存されずに終了した書き込みがあります。復元しますか？";
                if (startupFile != null) question += "\n\n「いいえ」を選ぶと、復元データを破棄して " + Path.GetFileName(startupFile) + " を開きます。";
                var answer = Modal(() => MessageBox.Show(bar, question, "Usugami", MessageBoxButton.YesNo, MessageBoxImage.Question));
                if (answer != MessageBoxResult.Yes) { DeleteRecovery(); return false; }
                bool restored = false;
                Run(delegate {
                    Scene.Load(RecoveryPath); Scene.Dirty = true; surface.InvalidateVisual(); restored = true;
                    Toast(startupFile == null ? "前回の書き込みを復元しました。必要なら保存してください" : "前回の書き込みを復元しました。" + Path.GetFileName(startupFile) + " は開いていません（「開く」から開けます）");
                });
                return restored;
            } finally { recoveryChecked = true; }
        }
        void DeleteRecovery() { try { if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public void SessionEnding() { try { if (Scene.Dirty && Scene.Items.Count > 0) { Directory.CreateDirectory(Settings.Folder); Scene.Write(RecoveryPath); } SaveSettings(); } catch (Exception) { } }
        BitmapSource RenderContent(bool flatten) {
            var world = Rect.Empty; foreach (var item in Scene.Items) world.Union(item.Bounds);
            if (world.IsEmpty) return null;
            var area = new Rect(Scene.Screen(world.TopLeft), Scene.Screen(world.BottomRight)); area.Inflate(16, 16);
            double scale = DeviceScale();
            double pixelWidth = Math.Ceiling(area.Width * scale), pixelHeight = Math.Ceiling(area.Height * scale);
            if (double.IsNaN(pixelWidth) || double.IsNaN(pixelHeight) || pixelWidth <= 0 || pixelHeight <= 0 || pixelWidth > Pictures.MaxSide || pixelHeight > Pictures.MaxSide || pixelWidth * pixelHeight > Pictures.MaxPixels) throw new InvalidOperationException("書き込みの範囲が大きすぎます。倍率を下げてから書き出してください。");
            int width = (int)pixelWidth, height = (int)pixelHeight;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                var board = Surface.BoardBrush(surface.Board);
                if (board != null || flatten) dc.DrawRectangle(board != null && surface.Board == Board.White ? board : flatten ? Brushes.White : board, null, new Rect(0, 0, area.Width, area.Height));
                dc.PushTransform(new TranslateTransform(Scene.Offset.X - area.X, Scene.Offset.Y - area.Y)); dc.PushTransform(new ScaleTransform(Scene.Zoom, Scene.Zoom));
                foreach (var item in Scene.Items) item.Draw(dc);
                dc.Pop(); dc.Pop();
            }
            var target = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); target.Render(visual); target.Freeze();
            return target;
        }
        static byte[] Png(BitmapSource bitmap) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var memory = new MemoryStream()) { encoder.Save(memory); return memory.ToArray(); } }
        void Export() {
            var image = RenderContent(false); if (image == null) { Toast("書き出す書き込みがありません"); return; }
            var dialog = new SaveFileDialog { Filter = "透明PNG|*.png", DefaultExt = ".png", FileName = (file == null ? "書き込み" : Path.GetFileNameWithoutExtension(file)) + ".png" };
            if (Modal(() => dialog.ShowDialog(bar)) != true) return;
            File.WriteAllBytes(dialog.FileName, Png(image));
            Toast("書き込みを透明PNGとして出力しました: " + Path.GetFileName(dialog.FileName));
        }
        void CopyImage() {
            var image = RenderContent(false); if (image == null) { Toast("コピーする書き込みがありません"); return; }
            // PNG keeps transparency for apps that understand it; the flattened bitmap is for everything else.
            var data = new DataObject();
            data.SetData("PNG", new MemoryStream(Png(image)), false);
            data.SetImage(RenderContent(true));
            WithClipboard(() => { Clipboard.SetDataObject(data, true); return true; });
            Toast("書き込みを画像としてコピーしました");
        }

        // ---------- Browser bridge ----------
        void CopyKey() { CopyText(token); Toast("接続キーをコピーしました。拡張機能の設定に貼り付けてください"); }
        void RegenerateKey() {
            if (Modal(() => MessageBox.Show(bar, "接続キーを再発行しますか？\n拡張機能側にも新しいキーを設定し直す必要があります。", "Usugami", MessageBoxButton.OKCancel, MessageBoxImage.Question)) != MessageBoxResult.OK) return;
            // An explicit reissue is persisted even over a settings file that could not be read at start.
            settings.Unreadable = false; settings.Key = token = Settings.NewKey(); bridge.Token = token; SaveSettings();
            CopyText(token); Toast("新しい接続キーを発行してコピーしました。拡張機能に貼り付けてください");
        }
        void ToggleFollow() {
            Follow = !Follow; follower.Reset();
            if (Follow) {
                if (Math.Abs(Scene.Zoom - 1) > 0.0001) Scene.ZoomAt(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2), 1 / Scene.Zoom);
                surface.InvalidateVisual();
                Toast(DateTime.UtcNow - bridge.LastSeen < TimeSpan.FromSeconds(4) ? "追従ON。操作モードでページをスクロールすると書き込みが付いてきます"
                    : DateTime.UtcNow - bridge.LastRejected < TimeSpan.FromSeconds(4) ? "追従ON。ただし拡張の接続キーが一致しません。「接続キーをコピー」して拡張に貼り直してください"
                    : "追従ON。ブラウザー拡張がまだ接続されていません（「使い方」を参照）");
            } else Toast("追従OFF。書き込みは画面に固定されます");
        }
        void OnScroll(ScrollMessage m) {
            if (!Follow) { follower.Reset(); return; }
            follower.Apply(Scene, m, DeviceScale()); surface.InvalidateVisual();
        }

        // ---------- Help ----------
        void ShowHelp() {
            if (help != null) { help.Activate(); return; }
            var stack = new StackPanel { Width = 540, Margin = new Thickness(6, 2, 6, 4) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            var close = new Button { Content = Theme.Icon("x", 14), Style = S("Btn"), ToolTip = "閉じる", MinWidth = 30, MinHeight = 30, Padding = new Thickness(6) }; DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
            var t = new StackPanel { Orientation = Orientation.Horizontal }; t.Children.Add(Theme.Logo(22));
            t.Children.Add(new TextBlock { Text = "Usugami の使い方", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center }); head.Children.Add(t);
            stack.Children.Add(head);
            Section(stack, "基本");
            Para(stack, "ツールを選ぶと「描画中」になり、画面の上に書き込めます。下のアプリやWebページを操作するときは Ctrl+Alt+A（またはツールバー左のボタン）で「操作中」に切り替えます。書き込みはそのまま残ります。");
            Section(stack, "ショートカット");
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var keys = new[] {
                new[] { "Ctrl+Alt+A", "描画 ↔ 下のアプリを操作" }, new[] { "Ctrl+Alt+H", "書き込みの表示 / 非表示" }, new[] { "Ctrl+Alt+Z", "元に戻す（いつでも）" }, new[] { "Ctrl+Alt+V", "クリップボードの画像を貼り付け" },
                new[] { "Esc", "選択解除 → 操作モードへ（描画中）" }, new[] { "Ctrl+Z / Ctrl+Y", "元に戻す / やり直す（描画中）" }, new[] { "Delete", "選択中の項目を削除（描画中）" }, new[] { "Ctrl+S", "上書き保存（描画中）" },
                new[] { "Shift + ドラッグ", "直線・矢印は45°単位、四角・楕円は縦横比固定" }, new[] { "ホイール / Shift+ホイール", "キャンバスを上下 / 左右に移動" }, new[] { "Ctrl + ホイール", "カーソル位置を中心に拡大縮小" }, new[] { "中ボタンドラッグ", "キャンバスを移動" } };
            for (int i = 0; i < keys.Length; i++) {
                grid.RowDefinitions.Add(new RowDefinition());
                var k = new Border { Background = Theme.Key, CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(0, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = keys[i][0], FontSize = 11.5 } };
                var d = new TextBlock { Text = keys[i][1], Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(k, i); Grid.SetRow(d, i); Grid.SetColumn(d, 1); grid.Children.Add(k); grid.Children.Add(d);
            }
            stack.Children.Add(grid);
            Section(stack, "Webページのスクロールに追従させる（Edge / Chrome）");
            Para(stack, "1. edge://extensions または chrome://extensions を開き、「開発者モード」をON。\n2. 「展開して読み込み」で Usugami に付属の extension フォルダーを指定。\n3. 下の「接続キーをコピー」→ 拡張機能アイコンの Usugami を開いて貼り付け、保存。\n4. Usugami の「ページ追従」をON。書き込んだら Ctrl+Alt+A で操作モードにしてスクロールします。");
            Para(stack, "接続キーは再起動しても変わりません。追従するのはページ全体のスクロールで、iframe・ページ内の独立したスクロール枠・PDF ビューアーは対象外です。");
            var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            buttons.Children.Add(Btn(Inline("key", "接続キーをコピー"), CopyKey, "拡張機能に貼り付けるキー", "Btn"));
            buttons.Children.Add(Btn(Inline("key", "キーを再発行"), RegenerateKey, "キーが漏れた場合などに新しいキーへ切り替え", "Btn"));
            buttons.Children.Add(Btn(Inline("help", "README を開く"), OpenReadme, "詳しい説明（README.md）", "Btn"));
            stack.Children.Add(buttons);
            var card = Theme.CardBorder(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); card.Padding = new Thickness(14, 10, 10, 12);
            help = new Window { Title = "Usugami の使い方", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight, MaxHeight = SystemParameters.WorkArea.Height, Foreground = Theme.Text, FontFamily = new FontFamily("Yu Gothic UI"), FontSize = 12.5, Content = card, WindowStartupLocation = WindowStartupLocation.CenterScreen, UseLayoutRounding = true };
            TextOptions.SetTextFormattingMode(help, TextFormattingMode.Display);
            head.MouseLeftButtonDown += delegate { try { help.DragMove(); } catch (InvalidOperationException) { } };
            close.Click += delegate { help.Close(); };
            help.PreviewKeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) help.Close(); };
            help.Closed += delegate { help = null; };
            if (!bar.IsVisible) ShowToolbar();
            help.Owner = bar; help.Show(); help.Activate();
        }
        static void Section(Panel p, string text) { p.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 12, 0, 6) }); }
        static void Para(Panel p, string text) { p.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Foreground = Theme.Muted, Margin = new Thickness(0, 0, 0, 4) }); }
        void OpenReadme() {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");
            if (!File.Exists(path)) path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "README.md"));
            if (!File.Exists(path)) { Toast("README.md が見つかりません"); return; }
            // .md often has no associated app; fall back to Notepad.
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { Process.Start("notepad.exe", "\"" + path + "\""); }
        }

        // ---------- Icons / settings / exit ----------
        static Stream IconStream() { return Assembly.GetExecutingAssembly().GetManifestResourceStream("Usugami.usugami.ico"); }
        static System.Drawing.Icon TrayIcon() {
            try { using (var s = IconStream()) if (s != null) return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize); } catch (Exception) { }
            return System.Drawing.SystemIcons.Application;
        }
        static ImageSource AppIcon() {
            try {
                using (var s = IconStream()) {
                    if (s == null) return null;
                    var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 48)).First(); frame.Freeze(); return frame;
                }
            } catch (Exception) { return null; }
        }
        // Returns false when the settings could not be written; the user is told, since a lost key breaks the browser link on the next start.
        bool SaveSettings() {
            if (settings.Unreadable) return false;
            if (bar != null) { settings.Left = bar.Left; settings.Top = bar.Top; }
            if (surface != null) { settings.Color = surface.Color; settings.Width = surface.PenWidth; }
            Exception error = null;
            for (int attempt = 0; attempt < 3; attempt++) {
                try { settings.Save(settingsPath); return true; }
                catch (Exception e) { error = e; Thread.Sleep(200); }
            }
            string text = "設定を保存できませんでした（" + error.Message.Trim() + "）。接続キーやツールバーの位置が次回に引き継がれない可能性があります";
            if (exiting && bar != null) MessageBox.Show(bar, text, "Usugami", MessageBoxButton.OK, MessageBoxImage.Warning);
            else { settingsWarning = text; if (status != null) { Toast(text); settingsWarning = null; } if (tray != null) tray.ShowBalloonTip(8000, "Usugami — 設定", text, Forms.ToolTipIcon.Warning); }
            return false;
        }
        void Exit() {
            if (exiting || !SaveIfNeeded()) return;
            exiting = true; DeleteRecovery(); SaveSettings();
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (statusTimer != null) statusTimer.Stop();
            UpdateKeys(); for (int i = 1; i <= GlobalKeys.Length; i++) Native.UnregisterHotKey(barHandle, i);
            if (bridge != null) bridge.Stop();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (help != null) help.Close();
            overlay.Close(); bar.Close(); app.Shutdown();
        }
    }
    class Owner32 : Forms.IWin32Window { readonly IntPtr handle; public Owner32(IntPtr handle) { this.handle = handle; } public IntPtr Handle { get { return handle; } } }

    class ScrollFollower {
        string page; double x, y, ratio; bool baseline;
        public void Reset() { baseline = false; }
        public void Apply(Scene scene, ScrollMessage m, double dpi) {
            bool reset = !baseline || page != m.Page || m.Kind == "reset" || Math.Abs(ratio - m.Ratio) > 0.001;
            if (!reset) scene.Pan(new Vector(-(m.X - x) * m.Ratio / dpi, -(m.Y - y) * m.Ratio / dpi));
            page = m.Page; x = m.X; y = m.Y; ratio = m.Ratio; baseline = true;
        }
    }

    [DataContract] class ScrollMessage {
        [DataMember(Name = "kind", IsRequired = true)] public string Kind { get; set; }
        [DataMember(Name = "page", IsRequired = true)] public string Page { get; set; }
        [DataMember(Name = "x", IsRequired = true)] public double X { get; set; }
        [DataMember(Name = "y", IsRequired = true)] public double Y { get; set; }
        [DataMember(Name = "ratio", IsRequired = true)] public double Ratio { get; set; }
    }
    // Loopback only, never CORS-enabled. Defense in depth against web pages and other local software:
    // a per-install secret (compared in constant time), a Host check against DNS rebinding,
    // rejection of any web-page Origin, an exclusive port bind and a cap on concurrent connections.
    class Bridge {
        public const int Port = 47831; const int MaxConnections = 16, RequestTimeout = 1500;
        volatile string token; readonly Action<ScrollMessage> receive; TcpListener listener; int port, active;
        public volatile bool Running; long lastSeenTicks, lastRejectedTicks;
        public string Token { get { return token; } set { token = value; } }
        public DateTime LastSeen { get { return new DateTime(Interlocked.Read(ref lastSeenTicks), DateTimeKind.Utc); } }
        // A well-formed extension request carrying the wrong key: the extension is installed but needs the current key.
        public DateTime LastRejected { get { return new DateTime(Interlocked.Read(ref lastRejectedTicks), DateTimeKind.Utc); } }
        public Bridge(string token, Action<ScrollMessage> receive) { this.token = token; this.receive = receive; }
        public void Start() { Start(Port); }
        public int Start(int requested) {
            listener = new TcpListener(IPAddress.Loopback, requested);
            // SO_EXCLUSIVEADDRUSE: no other process can bind the same port alongside us and intercept requests.
            listener.ExclusiveAddressUse = true;
            listener.Start(8); port = ((IPEndPoint)listener.LocalEndpoint).Port; Running = true;
            new Thread(Loop) { IsBackground = true, Name = "Usugami browser bridge" }.Start(); return port;
        }
        public void Stop() { Running = false; if (listener != null) listener.Stop(); }
        void Loop() {
            while (Running) {
                try {
                    var client = listener.AcceptTcpClient();
                    if (Interlocked.Increment(ref active) > MaxConnections) { Interlocked.Decrement(ref active); client.Close(); continue; }
                    ThreadPool.QueueUserWorkItem(delegate { try { Handle(client); } finally { Interlocked.Decrement(ref active); } });
                } catch (SocketException) { if (!Running) return; } catch (ObjectDisposedException) { return; }
            }
        }
        public static bool SameSecret(string a, string b) {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0;
        }
        void Handle(TcpClient client) {
            using (client) {
                var deadline = Stopwatch.StartNew();
                try {
                    client.ReceiveTimeout = RequestTimeout; client.SendTimeout = RequestTimeout;
                    var stream = client.GetStream(); var bytes = new List<byte>(); int c;
                    bool complete = false;
                    while (bytes.Count < 8192) {
                        SetReadTimeout(client, deadline); c = stream.ReadByte(); if (c < 0) return;
                        bytes.Add((byte)c); int n = bytes.Count;
                        if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) { complete = true; break; }
                    }
                    if (!complete || bytes.Any(b => b > 126 || (b < 32 && b != 13 && b != 10))) { Respond(stream, "400 Bad Request"); return; }
                    string headers = Encoding.ASCII.GetString(bytes.ToArray());
                    string[] lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines.Skip(1).Take(lines.Length - 3)) {
                        int i = line.IndexOf(':');
                        if (i <= 0 || !Regex.IsMatch(line.Substring(0, i), @"\A[!#$%&'*+.^_`|~0-9A-Za-z-]+\z") || fields.ContainsKey(line.Substring(0, i)) || line.Substring(i + 1).Any(ch => ch < 32 || ch == 127)) { Respond(stream, "400 Bad Request"); return; }
                        fields.Add(line.Substring(0, i), line.Substring(i + 1).Trim());
                    }
                    if (fields.ContainsKey("Transfer-Encoding")) { Respond(stream, "400 Bad Request"); return; }
                    string key, size, host, origin, contentType; int length;
                    bool hostOk = fields.TryGetValue("Host", out host) && (host == "127.0.0.1:" + port || host == "localhost:" + port);
                    // The extension's service worker sends a chrome-extension:// Origin; a web page would send its own.
                    bool originOk = !fields.TryGetValue("Origin", out origin) || Regex.IsMatch(origin, @"\Achrome-extension://[a-p]{32}\z");
                    bool keyOk = fields.TryGetValue("X-Usugami-Key", out key) && SameSecret(key, token);
                    if (lines[0] == "POST /scroll HTTP/1.1" && hostOk && originOk && key != null && !keyOk) Interlocked.Exchange(ref lastRejectedTicks, DateTime.UtcNow.Ticks);
                    if (lines[0] != "POST /scroll HTTP/1.1" || !hostOk || !originOk || !keyOk || !fields.TryGetValue("Content-Length", out size) || !int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 2 || length > 4096) { Respond(stream, "403 Forbidden"); return; }
                    if (!fields.TryGetValue("Content-Type", out contentType) || !(contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || contentType.Equals("application/json; charset=utf-8", StringComparison.OrdinalIgnoreCase))) { Respond(stream, "400 Bad Request"); return; }
                    var body = new byte[length]; int read = 0;
                    while (read < length) { SetReadTimeout(client, deadline); int count = stream.Read(body, read, length - read); if (count <= 0) return; read += count; }
                    ScrollMessage message;
                    try {
                        using (var input = JsonReaderWriterFactory.CreateJsonReader(body, new XmlDictionaryReaderQuotas { MaxDepth = 8, MaxStringContentLength = 1024, MaxArrayLength = 4096, MaxBytesPerRead = 4096, MaxNameTableCharCount = 1024 })) {
                            message = (ScrollMessage)new DataContractJsonSerializer(typeof(ScrollMessage)).ReadObject(input);
                            if (input.Read()) throw new SerializationException("余分な JSON データがあります。");
                        }
                    } catch (Exception e) { if (!(e is SerializationException || e is XmlException || e is ArgumentException)) throw; Respond(stream, "400 Bad Request"); return; }
                    if (message == null || string.IsNullOrEmpty(message.Page) || message.Page.Length > 300 || !Finite(message.X) || !Finite(message.Y) || !Finite(message.Ratio) || message.Ratio < 0.1 || message.Ratio > 10 || (message.Kind != "scroll" && message.Kind != "reset")) { Respond(stream, "400 Bad Request"); return; }
                    if (!Running || !SameSecret(key, token)) { Respond(stream, "403 Forbidden"); return; }
                    Interlocked.Exchange(ref lastSeenTicks, DateTime.UtcNow.Ticks); receive(message); Respond(stream, "204 No Content");
                } catch (Exception e) { if (!(e is IOException || e is SocketException || e is SerializationException || e is ObjectDisposedException || e is XmlException || e is ArgumentException || e is InvalidCastException)) Debug.WriteLine(e); }
                finally { CloseGracefully(client, deadline); }
            }
        }
        static void SetReadTimeout(TcpClient client, Stopwatch deadline) {
            long remaining = RequestTimeout - deadline.ElapsedMilliseconds;
            if (remaining <= 0) throw new IOException("リクエストの読み取りがタイムアウトしました。");
            client.ReceiveTimeout = (int)remaining;
        }
        static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n) && Math.Abs(n) < 1e9; }
        // Rejections reply before reading the body. Closing a socket with unread input makes Windows send RST,
        // which can discard the reply before the client reads it: the extension then saw "Usugami not found"
        // instead of "wrong key". Half-close and drain the rest so the reply always arrives.
        static void CloseGracefully(TcpClient client, Stopwatch deadline) {
            try {
                client.Client.Shutdown(SocketShutdown.Send);
                var sink = new byte[1024]; var stream = client.GetStream();
                for (int total = 0; total < 16384 && deadline.ElapsedMilliseconds < RequestTimeout; ) {
                    SetReadTimeout(client, deadline); int read = stream.Read(sink, 0, sink.Length);
                    if (read <= 0) break; total += read;
                }
            } catch (Exception e) { if (!(e is IOException || e is SocketException || e is ObjectDisposedException || e is InvalidOperationException)) Debug.WriteLine(e); }
        }
        static void Respond(Stream stream, string status) { var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); stream.Write(bytes, 0, bytes.Length); }
    }
    static class Native {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern IntPtr SetWindowLongPtr32(IntPtr h, int index, IntPtr value);
        // An owned window always stays above its owner, so clicks on the drawing canvas can never cover the toolbar.
        // Owner-hiding does not hide owned windows in Win32; WS_EX_APPWINDOW keeps the taskbar button.
        public static void OwnBy(IntPtr window, IntPtr owner) {
            if (IntPtr.Size == 8) SetWindowLongPtr64(window, -8, owner); else SetWindowLongPtr32(window, -8, owner);
            SetWindowLong(window, -20, GetWindowLong(window, -20) | 0x40000);
        }
        public static void Raise(IntPtr h) { if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x13); }
        public static void SetStyle(IntPtr hwnd, bool through) { int style = GetWindowLong(hwnd, -20) | 0x08000000 | 0x80; if (through) style |= 0x20; else style &= ~0x20; SetWindowLong(hwnd, -20, style); }
        // The overlay never takes keyboard focus, so WPF's Keyboard.Modifiers can be stale; ask the system instead.
        public static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
    }
    static class SelfTest {
        static int checks;
        public static void Run() {
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test.txt");
            try {
                var scene = new Scene(); scene.Remember();
                var tracking = new Scene(); var follower = new ScrollFollower();
                follower.Apply(tracking, new ScrollMessage { Kind = "reset", Page = "a", X = 0, Y = 500, Ratio = 1.5 }, 1.25);
                follower.Apply(tracking, new ScrollMessage { Kind = "scroll", Page = "a", X = 100, Y = 700, Ratio = 1.5 }, 1.25);
                Require(tracking.Offset == new Point(-120, -240), "scroll physical pixel scaling");
                follower.Apply(tracking, new ScrollMessage { Kind = "scroll", Page = "a", X = 0, Y = 500, Ratio = 1.5 }, 1.25);
                Require(tracking.Offset == new Point(0, 0), "reverse scroll returns to anchor");
                follower.Apply(tracking, new ScrollMessage { Kind = "scroll", Page = "b", X = 0, Y = 900, Ratio = 1.5 }, 1.25);
                Require(tracking.Offset == new Point(0, 0), "tab change baseline");
                TestBridge();
                var stroke = new StrokeItem { Color = Colors.Red, Width = 5 };
                stroke.Points.Add(new Mark(new Point(10, 20), 1)); stroke.Points.Add(new Mark(new Point(100, 20), 0.5)); scene.Items.Add(stroke);
                Require(stroke.Hit(new Point(50, 22), 2), "segment eraser"); Require(!stroke.Hit(new Point(50, 200), 2), "eraser miss");
                Require(!new StrokeItem { Width = 3 }.Hit(new Point(0, 0), 5), "empty stroke hit");
                scene.Undo(false); Require(scene.Items.Count == 0, "undo"); scene.Undo(true); Require(scene.Items.Count == 1, "redo");
                var cancelled = new Scene(); cancelled.Remember(); cancelled.Items.Add(stroke.Copy()); cancelled.Undo(false);
                cancelled.Remember(); cancelled.Forget(); Require(cancelled.CanRedo, "cancelled drawing preserves redo");
                cancelled.Undo(true); Require(cancelled.Items.Count == 1, "redo still works after cancelled drawing");
                var fullHistory = new Scene(); for (int i = 0; i < 100; i++) fullHistory.Remember();
                fullHistory.Remember(); fullHistory.Forget(); int undoCount = 0;
                while (fullHistory.CanUndo) { fullHistory.Undo(false); undoCount++; }
                Require(undoCount == 100, "cancelled drawing preserves capped undo history");
                scene.Dirty = false; scene.Remember(); scene.Forget(); Require(!scene.Dirty && scene.CanUndo, "forget restores dirty state");
                var anchor = scene.World(new Point(200, 300)); scene.ZoomAt(new Point(200, 300), 1.5); Require((anchor - scene.World(new Point(200, 300))).Length < 0.001, "zoom anchor");
                Require(scene.Dirty, "zooming saved content marks the saved view dirty");
                scene.Dirty = false; scene.Pan(new Vector(0, 20)); Require(scene.Dirty, "panning content marks the saved view dirty");
                scene.Dirty = false; scene.Pan(new Vector()); Require(!scene.Dirty, "unchanged view remains clean");
                var emptyView = new Scene(); emptyView.Pan(new Vector(10, 10)); Require(!emptyView.Dirty, "empty canvas movement needs no save prompt");
                var cancelledView = new Scene(); cancelledView.Items.Add(stroke.Copy()); cancelledView.Remember(); cancelledView.Pan(new Vector(0, 10)); cancelledView.Forget();
                Require(cancelledView.Dirty && !cancelledView.CanUndo, "cancelled drawing retains an interleaved view change");
                Require((scene.Screen(scene.World(new Point(37, 81))) - new Point(37, 81)).Length < 0.001, "screen/world inverse");
                var arrow = new ShapeItem { Kind = ShapeKind.Arrow, Start = new Point(0, 0), End = new Point(100, 0), Color = Colors.Blue, Width = 4 };
                Require(arrow.Hit(new Point(50, 3), 1) && arrow.Hit(new Point(99, 0), 1) && !arrow.Hit(new Point(50, 40), 1), "arrow hit test");
                var box = new ShapeItem { Kind = ShapeKind.Rectangle, Start = new Point(0, 0), End = new Point(100, 100), Color = Colors.Blue, Width = 4 };
                Require(box.Hit(new Point(0, 50), 2) && !box.Hit(new Point(50, 50), 2), "rectangle outline hit test");
                var text = new TextItem { Text = "説明\n2行目", Origin = new Point(10, 300), FontSize = 20, Color = Colors.Black };
                Require(text.Bounds.Height > 40 && text.Hit(new Point(15, 310), 0), "multi-line text bounds");
                var moved = (StrokeItem)stroke.Copy(); moved.Move(new Vector(5, 5)); Require(moved.Points[0].Position == new Point(15, 25) && stroke.Points[0].Position == new Point(10, 20), "move copies independently");
                var scaled = (ShapeItem)box.Copy(); scaled.Scale(new Point(0, 0), 2); Require(scaled.End == new Point(200, 200) && scaled.Width == 8, "uniform scale");
                scene.Items.Add(arrow); scene.Items.Add(text);
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8); bitmap.Freeze();
                scene.Items.Add(new ImageItem { Bitmap = bitmap, Rectangle = new Rect(50, 60, 100, 100) });
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-document." + Guid.NewGuid().ToString("N") + ".usugami");
                try {
                    scene.Save(path); var loaded = new Scene(); loaded.Load(path);
                    Require(loaded.Items.Count == 4 && loaded.Zoom == scene.Zoom && loaded.Offset == scene.Offset, "document round trip");
                    Require(((StrokeItem)loaded.Items[0]).Points[1].Pressure == 0.5, "pen pressure round trip");
                    var arrowBack = (ShapeItem)loaded.Items[1]; Require(arrowBack.Kind == ShapeKind.Arrow && arrowBack.End == new Point(100, 0) && arrowBack.Width == 4, "shape round trip");
                    var textBack = (TextItem)loaded.Items[2]; Require(textBack.Text == "説明\n2行目" && textBack.FontSize == 20 && textBack.Origin == new Point(10, 300), "text round trip");
                    Require(((ImageItem)loaded.Items[3]).Bitmap.PixelWidth == 2, "embedded image round trip"); Require(((ImageItem)loaded.Items[3]).EncodedBytes > 0, "embedded image size remembered");
                    var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { foreach (var item in loaded.Items) item.Draw(dc); foreach (var item in loaded.Items) item.Draw(dc); }
                    var target = new RenderTargetBitmap(300, 400, 96, 96, PixelFormats.Pbgra32); target.Render(visual);
                    var pixels = new byte[300 * 400 * 4]; target.CopyPixels(pixels, 300 * 4, 0);
                    Require(pixels.Where((b, i) => i % 4 == 3).Any(a => a != 0), "render draws pixels");
                    File.WriteAllText(path, "<layer version=\"1\" x=\"0\" y=\"0\" zoom=\"NaN\"/>");
                    bool rejected = false; try { loaded.Load(path); } catch (InvalidDataException) { rejected = true; }
                    Require(rejected && loaded.Items.Count == 4, "invalid document preserves scene");
                    File.WriteAllText(path, "<layer version=\"1\" x=\"0\" y=\"0\" zoom=\"1\"><shape kind=\"9\" color=\"#FF000000\" width=\"1\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"/></layer>");
                    rejected = false; try { loaded.Load(path); } catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "unknown shape kind rejected");
                    File.WriteAllText(path, "<layer version=\"1\" x=\"0\" y=\"0\" zoom=\"1\"><stroke color=\"ContextColor file://evil/profile.icc 1,0,0,0\" width=\"1\" marker=\"false\">0,0,1</stroke></layer>");
                    rejected = false; try { loaded.Load(path); } catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "context color URI rejected");
                    var bmp = Convert.ToBase64String(new BmpBitmapEncoder().With(bitmap));
                    File.WriteAllText(path, "<layer version=\"1\" x=\"0\" y=\"0\" zoom=\"1\"><image x=\"0\" y=\"0\" w=\"1\" h=\"1\">" + bmp + "</image></layer>");
                    rejected = false; try { loaded.Load(path); } catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "non-PNG embedded image rejected");
                    // A 67-byte PNG whose header claims 60000×60000 pixels must be refused before decoding.
                    var bomb = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0xEA, 0x60, 0, 0, 0xEA, 0x60, 8, 6, 0, 0, 0, 0, 0, 0, 0 };
                    rejected = false; try { Pictures.Decode(bomb, true); } catch (Exception) { rejected = true; }
                    Require(rejected, "oversized image header rejected");
                    rejected = false; try { Pictures.Check(15000, 15000); } catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "pixel budget enforced"); Pictures.Check(4000, 3000);
                    var big = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, new byte[400 * 300 * 4], 1600); big.Freeze();
                    var half = Pictures.Shrink(big, 0.5); Require(half.PixelWidth == 200 && half.PixelHeight == 150 && half.IsFrozen, "image shrink for document limits");
                    Require(Pictures.EncodedSize(big) > 0, "embedded PNG size estimate");
                    var placed = new List<Item> { new ImageItem { Bitmap = big, EncodedBytes = 1000 } };
                    Require(Pictures.PixelRoom(placed) == Pictures.MaxPixels - 120000 && Pictures.ByteRoom(placed) == (Scene.MaxDocumentBytes * 95L / 100 - 200 - 1336) * 3 / 4, "room left for new images");
                    File.WriteAllText(path, "<layer version=\"1\" x=\"0\" y=\"0\" zoom=\"1\"><stroke color=\"#FF00FF00\" width=\"2\" marker=\"false\">0,0,1 5,5,1</stroke></layer>");
                    loaded.Remember(); loaded.Load(path);
                    Require(loaded.Items.Count == 1 && ((StrokeItem)loaded.Items[0]).Color == Colors.Lime && !loaded.CanUndo && !loaded.Dirty, "legacy file opens with a fresh history");
                    Require(Scene.IsDocument("a.USUGAMI") && Scene.IsDocument("b.layer") && !Scene.IsDocument("c.png"), "document extensions");
                    TestUntrustedDocuments(loaded, path, bitmap);
                } finally { if (File.Exists(path)) File.Delete(path); }
                string settingsPath = Path.Combine(Path.GetTempPath(), "usugami-settings-" + Guid.NewGuid().ToString("N") + ".xml");
                try {
                    var key = Settings.NewKey(); Require(Settings.ValidKey(key) && key != Settings.NewKey() && !Settings.ValidKey("ABC"), "connection key format");
                    new Settings { Key = key, Left = 120.5, Top = 40, Width = 7, Color = Colors.Teal, Compact = true }.Save(settingsPath);
                    var s = Settings.Load(settingsPath); Require(s.Key == key && s.Left == 120.5 && s.Top == 40 && s.Width == 7 && s.Color == Colors.Teal && s.Compact, "settings round trip");
                    File.WriteAllText(settingsPath, "garbage"); s = Settings.Load(settingsPath); Require(s.Key == null && double.IsNaN(s.Left), "corrupt settings fall back to defaults");
                    new Settings { Key = key }.Save(settingsPath);
                    using (new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None)) { s = Settings.Load(settingsPath); }
                    Require(s.Unreadable && s.Key == null, "locked settings are reported, not replaced");
                    s = Settings.Load(settingsPath); Require(!s.Unreadable && s.Key == key, "settings read once the lock is released");
                    File.WriteAllText(settingsPath, "<settings width=\"NaN\" left=\"Infinity\" top=\"1e300\"/>"); s = Settings.Load(settingsPath);
                    Require(s.Width == 4 && double.IsNaN(s.Left) && double.IsNaN(s.Top), "nonfinite and extreme settings rejected");
                    Require(!Settings.ValidKey(key + "\n"), "connection key must match the entire string");
                    string original = File.ReadAllText(settingsPath); bool rejected = false;
                    try { SafeXml.Save(new XDocument(new XElement("too-large", new string('x', 100))), settingsPath, 10); } catch (InvalidDataException) { rejected = true; }
                    Require(rejected && File.ReadAllText(settingsPath) == original, "failed atomic save preserves original file");
                } finally { if (File.Exists(settingsPath)) File.Delete(settingsPath); }
                Require(Theme.Load().Contains("Btn"), "theme resources parse");
                foreach (var pair in Theme.Icons) Require(!Geometry.Parse(pair.Value).Bounds.IsEmpty, "icon " + pair.Key);
                File.WriteAllText(output, "PASS (" + checks + " checks): scroll tracking, loopback authentication + strict HTTP/JSON + request deadline, editing + undo/redo, document round trip + render, invalid document atomicity + XML/point/text/image budgets, image header + pixel limits, settings + atomic save, theme + icons.\r\n");
            } catch (Exception e) { File.WriteAllText(output, "FAIL: " + e); Environment.ExitCode = 1; }
        }
        static void Require(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
        static string Document(string body) { return "<usugami version=\"1\" x=\"0\" y=\"0\" zoom=\"1\">" + body + "</usugami>"; }
        static void RejectDocument(Scene scene, string path, string xml, string name) {
            var items = scene.Items; var offset = scene.Offset; double zoom = scene.Zoom; bool dirty = scene.Dirty, undo = scene.CanUndo, redo = scene.CanRedo;
            File.WriteAllText(path, xml); bool rejected = false;
            try { scene.Load(path); } catch (InvalidDataException) { rejected = true; }
            Require(rejected && ReferenceEquals(items, scene.Items) && offset == scene.Offset && zoom == scene.Zoom && dirty == scene.Dirty && undo == scene.CanUndo && redo == scene.CanRedo, name + " preserves scene and history");
        }
        static void TestUntrustedDocuments(Scene scene, string path, BitmapSource bitmap) {
            scene.Remember();
            const string textStart = "<text color=\"#FF000000\" size=\"20\" x=\"0\" y=\"0\">";
            const string strokeStart = "<stroke color=\"#FF000000\" width=\"2\" marker=\"false\">";
            RejectDocument(scene, path, "<!DOCTYPE usugami [<!ENTITY x 'injected'>]>" + Document(textStart + "&x;</text>"), "DTD/entity rejected");
            RejectDocument(scene, path, Document("<unknown/>"), "unknown element rejected");
            RejectDocument(scene, path, Document(textStart + "a<nested><deeper>b</deeper></nested></text>"), "nested XML rejected");
            RejectDocument(scene, path, Document(textStart.Replace("size=\"20\"", "size=\"1000000\"") + "a</text>"), "oversized font rejected before rendering");
            RejectDocument(scene, path, Document(textStart.Replace("#FF000000", "#FF000000&#10;") + "a</text>"), "color trailing newline rejected");
            RejectDocument(scene, path, Document(strokeStart + "0,0,1.1</stroke>"), "out-of-range pressure rejected");
            RejectDocument(scene, path, Document(strokeStart + "</stroke>"), "empty stroke rejected");
            RejectDocument(scene, path, Document(strokeStart + new string(',', 10000) + "</stroke>"), "oversized point token rejected");
            RejectDocument(scene, path, Document(strokeStart + "0,0,1,2</stroke>"), "extra point fields rejected");
            RejectDocument(scene, path, Document("<text " + string.Join(" ", Enumerable.Range(0, 17).Select(i => "a" + i + "=\"x\"")) + ">a</text>"), "XML attribute budget");
            RejectDocument(scene, path, Document(strokeStart + string.Concat(Enumerable.Repeat("0,0,1 ", Scene.MaxPoints + 1)) + "</stroke>"), "stroke point budget");
            RejectDocument(scene, path, Document(string.Concat(Enumerable.Repeat(textStart + new string('a', Scene.MaxTextLength) + "</text>", Scene.MaxTextCharacters / Scene.MaxTextLength + 1))), "total text budget");
            RejectDocument(scene, path, Document(string.Concat(Enumerable.Repeat(textStart + "a</text>", Scene.MaxItems + 1))), "item count budget");
            bool rejected = false; try { Pictures.Decode(new PngBitmapEncoder().With(bitmap), true, 3); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "remaining image pixel budget checked before full decode");
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write)) file.SetLength(Scene.MaxDocumentBytes + 1L);
            rejected = false; try { scene.Load(path); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "oversized document rejected before XML parsing");
            rejected = false; try { Pictures.Load(path); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "oversized image file rejected before decode");
            // A scene exceeding the reader limits must never overwrite a valid saved file.
            File.WriteAllText(path, "original"); var oversized = new Scene(); oversized.Items.Add(new TextItem { Text = new string('a', Scene.MaxTextLength + 1), Color = Colors.Black });
            rejected = false; try { oversized.Save(path); } catch (InvalidDataException) { rejected = true; }
            Require(rejected && File.ReadAllText(path) == "original", "unsavable scene preserves original document");
        }
        static byte[] With(this BitmapEncoder encoder, BitmapSource bitmap) { encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var m = new MemoryStream()) { encoder.Save(m); return m.ToArray(); } }
        static void TestBridge() {
            ScrollMessage received = null; using (var arrived = new ManualResetEvent(false)) {
                var bridge = new Bridge("test-key", delegate(ScrollMessage m) { received = m; arrived.Set(); });
                int port = bridge.Start(0);
                try {
                    Require(Post(port, "wrong-key") == 403 && received == null, "bridge rejects bad key");
                    Require(bridge.LastRejected > DateTime.UtcNow.AddSeconds(-10), "bridge records a wrong key");
                    Require(Post(port, "test-key") == 204 && arrived.WaitOne(1000), "bridge delivers request");
                    Require(received.Page == "test" && received.Y == 250 && received.Ratio == 1.25, "bridge JSON values");
                    bridge.Token = "new-key"; Require(Post(port, "test-key") == 403 && Post(port, "new-key") == 204, "bridge key rotation");
                    Require(Post(port, "new-key", "attacker.example:" + port, null) == 403, "bridge rejects rebinding Host");
                    Require(Post(port, "new-key", null, "https://attacker.example") == 403, "bridge rejects web page Origin");
                    Require(Post(port, "new-key", null, "chrome-extension://abcdefghijklmnopabcdefghijklmnop") == 204, "bridge accepts extension Origin");
                    Require(Post(port, "new-key", null, "chrome-extension://abcdefghijklmnop.attacker.example") == 403, "bridge rejects malformed extension Origin");
                    const string json = "{\"kind\":\"scroll\",\"page\":\"test\",\"x\":0,\"y\":250,\"ratio\":1.25}";
                    Require(RawStatus(port, Request(port, "new-key", json, "X-Usugami-Key: new-key\r\n")) == 400, "duplicate authentication header rejected");
                    Require(RawStatus(port, Request(port, "new-key", json, "Host: attacker.example\r\n")) == 400, "duplicate Host rejected");
                    Require(RawStatus(port, Request(port, "new-key", json, "Transfer-Encoding: chunked\r\n")) == 400, "ambiguous request framing rejected");
                    Require(RawStatus(port, Request(port, "new-key", json).Replace("Content-Type: application/json\r\n", "")) == 400, "missing content type rejected");
                    Require(RawStatus(port, Request(port, "new-key", json).Replace("POST /scroll", "OPTIONS /scroll")) == 403, "CORS preflight rejected");
                    Require(RawStatus(port, Request(port, "new-key", json.Replace("\"x\":0,", ""))) == 400, "missing JSON member rejected");
                    Require(LateReadStatus(port, Request(port, "wrong-key", json)) == 403, "rejected reply survives a client that reads late");
                    Require(RawStatus(port, Request(port, "new-key", json.Replace("\"x\":0,", "\"x\":0,\"x\":1,"))) == 400, "duplicate JSON member rejected");
                    Require(RawStatus(port, Request(port, "new-key", json + " trailing")) == 400, "trailing JSON data rejected");
                    Require(RawStatus(port, Request(port, "new-key", "{")) == 403, "invalid body length rejected");
                    Require(RawStatus(port, Request(port, "new-key", "{}")) == 400, "invalid JSON contract rejected");
                    Require(RawStatus(port, Request(port, "new-key", json.Replace("250", "1e300"))) == 400, "extreme scroll coordinate rejected");
                    Require(RawStatus(port, "POST /scroll HTTP/1.1\r\nX-Fill: " + new string('a', 8192)) == 400, "unterminated oversized header rejected");
                    TestRequestDeadline(port);
                    Require(Bridge.SameSecret("abc", "abc") && !Bridge.SameSecret("abc", "abd") && !Bridge.SameSecret("abc", "abcd") && !Bridge.SameSecret(null, "a"), "constant-time compare");
                } finally { bridge.Stop(); }
            }
        }
        static string Request(int port, string key, string json, string extra) {
            return "POST /scroll HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nX-Usugami-Key: " + key + "\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(json) + "\r\n" + extra + "\r\n" + json;
        }
        static string Request(int port, string key, string json) { return Request(port, key, json, ""); }
        // Reads only after the server has replied and closed: an RST on close would have discarded the reply.
        static int LateReadStatus(int port, string request) {
            using (var client = new TcpClient()) {
                client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = 3000;
                var bytes = Encoding.UTF8.GetBytes(request); var stream = client.GetStream(); stream.Write(bytes, 0, bytes.Length);
                Thread.Sleep(400);
                try { using (var reader = new StreamReader(stream, Encoding.ASCII)) { var line = reader.ReadLine(); return line == null ? 0 : int.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture); } }
                catch (IOException) { return -1; }
            }
        }
        static int RawStatus(int port, string request) {
            using (var client = new TcpClient()) {
                client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = 3000; client.SendTimeout = 3000;
                var bytes = Encoding.UTF8.GetBytes(request); var stream = client.GetStream(); stream.Write(bytes, 0, bytes.Length);
                using (var reader = new StreamReader(stream, Encoding.ASCII)) { var line = reader.ReadLine(); return line == null ? 0 : int.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture); }
            }
        }
        static void TestRequestDeadline(int port) {
            using (var client = new TcpClient()) {
                client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = 1000;
                var stream = client.GetStream(); stream.WriteByte((byte)'P');
                Thread.Sleep(900); stream.WriteByte((byte)'O'); Thread.Sleep(900);
                bool closed = false; try { closed = stream.ReadByte() < 0; } catch (IOException) { closed = false; }
                Require(closed, "slow trickle cannot extend the request deadline");
            }
        }
        static int Post(int port, string key) { return Post(port, key, null, null); }
        static int Post(int port, string key, string host, string origin) {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/scroll"); request.Proxy = null; request.Method = "POST"; request.ContentType = "application/json"; request.ServicePoint.Expect100Continue = false; request.Timeout = 3000; request.Headers.Add("X-Usugami-Key", key);
            if (host != null) request.Host = host;
            if (origin != null) request.Headers.Add("Origin", origin);
            var bytes = Encoding.UTF8.GetBytes("{\"kind\":\"scroll\",\"page\":\"test\",\"x\":0,\"y\":250,\"ratio\":1.25}"); request.ContentLength = bytes.Length;
            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            try { using (var response = (HttpWebResponse)request.GetResponse()) return (int)response.StatusCode; }
            catch (WebException e) { if (e.Response == null) throw; using (var response = (HttpWebResponse)e.Response) return (int)response.StatusCode; }
        }
    }
}
