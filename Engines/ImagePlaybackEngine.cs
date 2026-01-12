#nullable enable
using CinecorePlayer2025;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CinecorePlayer2025.Engines
{
    /// <summary>
    /// Implementazione "finta" di IPlaybackEngine per file immagine.
    /// Non usa DirectShow, disegna la bitmap dentro l'HWND passato a UpdateVideoWindow.
    /// Le immagini vengono renderizzate 1:1 (nessuno scaling), centrate nel pannello.
    /// </summary>
    internal sealed class ImagePlaybackEngine : IPlaybackEngine
    {
        private Bitmap? _bitmap;
        private string? _path;
        private Rectangle _lastDest;
        private Action? _updateCb;

        public event Action<double>? OnProgressSeconds;
        public event Action<string>? OnStatus;
        public event Action<bool>? OnBitstreamChanged;

        public static readonly string[] SupportedExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        public static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(SupportedExtensions, ext) >= 0;
        }

        public void Open(string mediaPath, bool hasVideo)
        {
            DisposeBitmap();

            _path = mediaPath;

            // Carica la bitmap evitando di bloccare il file su disco
            using (var fs = new FileStream(mediaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var img = Image.FromStream(fs))
            {
                _bitmap = new Bitmap(img);
            }

            OnStatusSafe("Immagine: " + Path.GetFileName(mediaPath));
            // Nessun tempo di riproduzione, niente timer di progress
            _updateCb?.Invoke();
        }

        public void Play() { /* no-op per le foto */ }
        public void Pause() { /* no-op */ }
        public void Stop() { /* no-op */ }

        public double DurationSeconds => 0;

        public double PositionSeconds
        {
            get => 0;
            set { /* ignorato per le foto */ }
        }

        public void SetVolume(float volume)
        {
            // niente audio nelle foto → noop
        }

        /// <summary>
        /// Disegna l'immagine alla sua risoluzione nativa, centrata nel rettangolo client.
        /// Nessuno scaling: 1:1 pixel. Se l'immagine è più grande del pannello viene semplicemente "tagliata".
        /// </summary>
        public void UpdateVideoWindow(nint ownerHwnd, Rectangle ownerClient)
        {
            if (_bitmap == null) return;
            if (ownerClient.Width <= 0 || ownerClient.Height <= 0) return;

            try
            {
                using var g = Graphics.FromHwnd(ownerHwnd);
                g.Clear(Color.Black);

                int imgW = _bitmap.Width;
                int imgH = _bitmap.Height;

                // Dimensioni di destinazione = dimensioni originali (no scaling)
                int dstW = imgW;
                int dstH = imgH;

                // Centro l'immagine nel pannello
                int dx = (ownerClient.Width - dstW) / 2;
                int dy = (ownerClient.Height - dstH) / 2;

                // Coordinate client (0,0 è l'angolo in alto a sinistra del pannello)
                var dest = new Rectangle(dx, dy, dstW, dstH);

                // Disegno non scalato per evitare qualsiasi interpolazione
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                // Nota: ownerClient.X/Y per questo engine sono sempre 0, quindi non servono trasformazioni.
                // Se in futuro passi un rettangolo non 0,0 puoi aggiungere una TranslateTransform.
                g.DrawImageUnscaled(_bitmap, dest.Location);

                // Salvo la dest rect in coordinate client del pannello, clippata ai bordi
                var clientRect = new Rectangle(0, 0, ownerClient.Width, ownerClient.Height);
                var visible = Rectangle.Intersect(dest, clientRect);
                _lastDest = visible.Width > 0 && visible.Height > 0 ? visible : clientRect;
            }
            catch
            {
                // niente eccezioni verso l'esterno
            }
        }

        public Rectangle GetLastDestRectAsClient(Rectangle ownerClient)
        {
            if (_lastDest.Width <= 0 || _lastDest.Height <= 0)
                return new Rectangle(0, 0, ownerClient.Width, ownerClient.Height);
            return _lastDest;
        }

        public void SetStereo3D(Stereo3DMode mode)
        {
            // non ha senso per le foto, ignora
        }

        public void SetUpscaling(bool enable)
        {
            // Ignorato: le foto sono sempre renderizzate 1:1 (nessuno scaling).
        }

        public void BindUpdateCallback(Action cb) => _updateCb = cb;

        public bool IsBitstreamActive() => false;

        public bool HasDisplayControl() => true; // disegna lui stesso

        public (string text, DateTime when) GetLastVideoMTDump()
            => ("Image file - nessun MediaType DirectShow", DateTime.MinValue);

        public (int width, int height, string subtype) GetNegotiatedVideoFormat()
        {
            if (_bitmap == null) return (0, 0, "image");
            return (_bitmap.Width, _bitmap.Height, "image");
        }

        public (int bytes, DateTime when) GetLastSnapshotInfo()
            => (0, DateTime.MinValue);

        public List<DsStreamItem> EnumerateStreams() => new();

        public bool EnableByGlobalIndex(int globalIndex) => false;

        public bool DisableSubtitlesIfPossible() => false;

        /// <summary>
        /// Volendo potresti serializzare la bitmap a BMP/JPEG in memoria.
        /// Per ora ritorna sempre false.
        /// </summary>
        public bool TrySnapshot(out int byteCount)
        {
            byteCount = 0;
            return false;
        }

        public Bitmap? GetPreviewFrame(double seconds, int maxW = 360)
        {
            if (_bitmap == null) return null;
            try
            {
                double scale = maxW / (double)_bitmap.Width;
                int w = maxW;
                int h = (int)Math.Round(_bitmap.Height * scale);

                var thumb = new Bitmap(w, h);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_bitmap, new Rectangle(0, 0, w, h));
                }
                return thumb;
            }
            catch
            {
                return null;
            }
        }

        // madVR: per le foto non ha senso → noop
        public void SetMadVrChroma(MadVrCategoryPreset preset) { }
        public void SetMadVrImageUpscale(MadVrCategoryPreset preset) { }
        public void SetMadVrImageDownscale(MadVrCategoryPreset preset) { }
        public void SetMadVrRefinement(MadVrCategoryPreset preset) { }
        public void SetMadVrFps(MadVrFpsChoice choice) { }
        public void SetMadVrHdrMode(MadVrHdrMode mode) { }

        public void Dispose()
        {
            DisposeBitmap();
        }

        private void DisposeBitmap()
        {
            try { _bitmap?.Dispose(); } catch { }
            _bitmap = null;
            _path = null;
            _lastDest = Rectangle.Empty;
        }

        private void OnStatusSafe(string s)
        {
            try { OnStatus?.Invoke(s); }
            catch { }
        }
    }
}
