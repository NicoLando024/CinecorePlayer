using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using System;

#nullable enable

namespace CinecorePlayer2025
{
    internal sealed partial class MediaLibraryPage
    {
        // ------------ PictureBox DB per thumbnails ------------
        private sealed class DBPictureBox : PictureBox
        {
            public DBPictureBox()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
                DoubleBuffered = true;
                BackColor = Color.Black;
                SizeMode = PictureBoxSizeMode.Zoom;
            }
        }


        // ------------ THUMB GENERATION / PLACEHOLDER ------------
        private void BeginThumbLoadForCard(FileCard card, string path, CancellationToken ct)
        {
            // Applichiamo la logica "intelligente" SOLO quando siamo nella categoria Film.
            bool isFilmCategory = string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase);

            if (!isFilmCategory)
            {
                // per tutte le altre categorie usiamo la pipeline standard
                card.BeginThumbLoad(ct);
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested || card.IsDisposed)
                        return;

                    // chiede a MovieMetadataService: titolo normalizzato + eventuale poster locale
                    var (title, year, posterPath) = MovieMetadataService.ResolveTitleAndPoster(path, ct);

                    // aggiorna il titolo visualizzato, se abbiamo qualcosa
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        card.BeginInvoke(new Action(() =>
                        {
                            if (!card.IsDisposed)
                                card.SetDisplayName(title);
                        }));
                    }

                    Bitmap? bmp = null;

                    // se abbiamo un poster locale, proviamo a usarlo
                    if (!string.IsNullOrWhiteSpace(posterPath) && File.Exists(posterPath))
                    {
                        try
                        {
                            using var src = new Bitmap(posterPath);
                            bmp = ResizeBitmap(src, Math.Max(520, card.Width));
                        }
                        catch
                        {
                            bmp = null;
                        }
                    }

                    // fallback: thumb dal file come prima
                    // fallback: usa il meccanismo standard della card
                    if (bmp == null)
                    {
                        card.BeginInvoke(new Action(() =>
                        {
                            if (!card.IsDisposed)
                                card.BeginThumbLoad(ct);
                        }));
                        return;
                    }

                    if (bmp == null || ct.IsCancellationRequested || card.IsDisposed)
                        return;

                    card.BeginInvoke(new Action(() =>
                    {
                        if (!card.IsDisposed)
                            card.SetInitialPlaceholder(bmp);
                    }));
                }
                catch
                {
                    // se qualcosa va storto, torniamo alla pipeline base
                    if (ct.IsCancellationRequested || card.IsDisposed)
                        return;

                    card.BeginInvoke(new Action(() =>
                    {
                        if (!card.IsDisposed)
                            card.BeginThumbLoad(ct);
                    }));
                }
            }, ct);
        }
        private static Bitmap ResizeBitmap(Bitmap src, int maxW)
        {
            if (src.Width <= maxW)
                return new Bitmap(src);
            var h = (int)Math.Max(1, src.Height * (maxW / (double)src.Width));
            var bmp = new Bitmap(maxW, h);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, maxW, h);
            return bmp;
        }

        private static bool IsMostlyDark(Bitmap bmp)
        {
            // se >90% pixel sotto 20/255 la consideriamo nera
            int darkCount = 0;
            int total = 0;

            int stepX = Math.Max(1, bmp.Width / 8);
            int stepY = Math.Max(1, bmp.Height / 8);

            for (int y = stepY / 2; y < bmp.Height; y += stepY)
            {
                for (int x = stepX / 2; x < bmp.Width; x += stepX)
                {
                    var c = bmp.GetPixel(x, y);
                    int lum = (c.R + c.G + c.B) / 3;
                    total++;
                    if (lum < 20) darkCount++;
                }
            }

            if (total == 0) return false;
            double ratioDark = darkCount / (double)total;
            return ratioDark > 0.9;
        }
        private static Bitmap? TryLoadThumb(string path, int maxW)
        {
            try
            {
                var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();

                // immagini → carica diretta
                if (new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".webp" }.Contains(ext))
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var src = new Bitmap(fs);
                    var bmpImg = ResizeBitmap(src, maxW);

                    // frame quasi nero? scarta
                    if (IsMostlyDark(bmpImg))
                    {
                        bmpImg.Dispose();
                        return null;
                    }

                    return bmpImg;
                }

                // video/audio → estrai frame (Thumbnailer custom)
                try
                {
                    var th = new Thumbnailer(); // tua classe esterna
                    th.Open(path);
                    var frame = th.Get(seconds: 3.0, maxW: maxW)
                               ?? th.Get(1.0, maxW);

                    if (frame != null)
                    {
                        if (IsMostlyDark(frame))
                        {
                            frame.Dispose();
                            frame = null;
                        }

                        if (frame != null)
                            return frame;
                    }
                }
                catch
                {
                    // se il thumbnailer fallisce, andiamo al fallback sotto
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string CategoryFromExt(string ext)
        {
            if (new[] { ".mkv" }.Contains(ext)) return "film";
            if (new[] { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m2ts", ".ts", ".iso" }.Contains(ext)) return "video";
            if (new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".webp" }.Contains(ext)) return "foto";
            if (new[]
                { ".mp3", ".flac", ".mka", ".aac", ".ogg", ".wav", ".wma",
                  ".m4a", ".opus", ".dts", ".ac3", ".eac3" }.Contains(ext)) return "musica";
            return "video";
        }

        private static Bitmap GetCategoryPlaceholder(string cat, int maxW)
        {
            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025",
                    "placeholders");

                var file = cat switch
                {
                    "film" => Path.Combine(folder, "film.jpg"),
                    "video" => Path.Combine(folder, "video.jpg"),
                    "musica" => Path.Combine(folder, "musica.jpg"),
                    "foto" => Path.Combine(folder, "foto.jpg"),
                    _ => Path.Combine(folder, "video.jpg")
                };

                if (File.Exists(file))
                {
                    using var src = new Bitmap(file);
                    return ResizeBitmap(src, maxW);
                }
            }
            catch { }

            // fallback gradient + emoji
            var w = maxW;
            var h = Math.Max(120, (int)(maxW * 9.0 / 16.0));
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            (Color c1, Color c2, string icon) tpl = cat switch
            {
                "film" => (Color.FromArgb(80, 30, 180), Color.FromArgb(30, 10, 90), "🎬"),
                "video" => (Color.FromArgb(0, 140, 200), Color.FromArgb(0, 70, 120), "▶"),
                "musica" => (Color.FromArgb(0, 170, 120), Color.FromArgb(0, 90, 70), "♪"),
                "foto" => (Color.FromArgb(190, 120, 0), Color.FromArgb(120, 70, 0), "🖼"),
                _ => (Color.FromArgb(60, 60, 60), Color.FromArgb(30, 30, 30), "■")
            };

            using (var lg = new LinearGradientBrush(
                new Rectangle(0, 0, w, h),
                tpl.c1,
                tpl.c2,
                LinearGradientMode.ForwardDiagonal))
            {
                g.FillRectangle(lg, 0, 0, w, h);
            }

            using var f = new Font("Segoe UI Emoji", Math.Max(28, w / 10), FontStyle.Bold);
            var sz = g.MeasureString(tpl.icon, f);
            g.DrawString(tpl.icon, f, Brushes.White,
                (w - sz.Width) / 2f,
                (h - sz.Height) / 2f);

            return bmp;
        }

        private static Bitmap GetEmptyStateImage(string category, int maxW)
        {
            string cat = category.Trim().ToLowerInvariant();
            string fileName = cat switch
            {
                "film" => "film",
                "video" => "video",
                "foto" => "foto",
                "musica" => "musica",
                _ => "video"
            };

            // 1) assets locali accanto all’eseguibile
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var probeDirs = new[]
            {
        Path.Combine(appDir, "assets", "empty"),
        // 2) cartella utente (override)
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "CinecorePlayer2025", "empties"),
        // 3) fallback: vecchie "placeholders"
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "CinecorePlayer2025", "placeholders")
    };

            var probeExts = new[] { ".png", ".jpg", ".jpeg", ".webp" };

            try
            {
                foreach (var dir in probeDirs)
                {
                    foreach (var ext in probeExts)
                    {
                        var f = Path.Combine(dir, fileName + ext);
                        if (File.Exists(f))
                        {
                            using var src = new Bitmap(f);
                            return ResizeBitmap(src, maxW);
                        }
                    }
                }
            }
            catch { /* fallback sotto */ }

            // se non troviamo un asset dedidcato, usa il placeholder di categoria
            return GetCategoryPlaceholder(fileName, maxW);
        }


    }
}
