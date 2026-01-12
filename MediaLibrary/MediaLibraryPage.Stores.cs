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
        // ------------ RECENTS STORE ------------
        private sealed class RecentsStore
        {
            private sealed class Model
            {
                public Dictionary<string, List<string>> Items { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private Model _data;

            public RecentsStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "recents.json");
                _data = Load();
            }

            public IEnumerable<string> TryGet(string category)
            {
                if (_data.Items.TryGetValue(category, out var list))
                    return list;
                return Array.Empty<string>();
            }

            public void Set(string category, List<string> paths)
            {
                _data.Items[category] = paths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Save();
            }

            public void PruneToCategory(string category, string[] allowedExts)
            {
                if (!_data.Items.TryGetValue(category, out var list))
                    return;

                var allow = new HashSet<string>(allowedExts, StringComparer.OrdinalIgnoreCase);

                var filtered = list
                    .Where(p =>
                    {
                        var ext = Path.GetExtension(p) ?? "";
                        return allow.Contains(ext);
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(200)
                    .ToList();

                _data.Items[category] = filtered;
                Save();
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null) return m;
                    }
                }
                catch { }
                return new Model();
            }

            private void Save()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch { }
            }
        }


        // ------------ FAVORITES STORE ------------
        private sealed class FavoritesStore
        {
            private sealed class Model
            {
                public HashSet<string> Paths { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private Model _data;
            private readonly object _lock = new();

            public FavoritesStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "favorites.json");
                _data = Load();
            }
            public bool IsFav(string path)
            {
                lock (_lock)
                    return _data.Paths.Contains(path);
            }
            public IEnumerable<string> All()
            {
                lock (_lock)
                    return _data.Paths.ToArray();
            }
            public void Set(string path, bool fav)
            {
                lock (_lock)
                {
                    if (fav) _data.Paths.Add(path);
                    else _data.Paths.Remove(path);

                    Save();
                }
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null) return m;
                    }
                }
                catch { }
                return new Model();
            }
            private void Save()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch { }
            }
        }


        // ------------ MUSIC RECENTS STORE (ultimi 30 brani riprodotti) ------------
        private sealed class MusicRecentsStore
        {
            private sealed class Model
            {
                public List<string> Items { get; set; } = new();
            }

            private readonly string _file;
            private readonly object _lock = new();
            private Model _data;

            public MusicRecentsStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "musicRecents.json");
                _data = Load();
            }

            public IReadOnlyList<string> All()
            {
                lock (_lock)
                {
                    // pulizia base: niente stringhe vuote / doppioni e max 30
                    _data.Items = _data.Items
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(30)
                        .ToList();

                    SaveNoLock();
                    return _data.Items.ToList();
                }
            }

            public void RegisterPlay(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                lock (_lock)
                {
                    // rimuovi se già presente
                    _data.Items.RemoveAll(p =>
                        string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

                    // inserisci in testa
                    _data.Items.Insert(0, path);

                    // mantieni solo gli ultimi 30
                    if (_data.Items.Count > 30)
                        _data.Items.RemoveRange(30, _data.Items.Count - 30);

                    SaveNoLock();
                }
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null) return m;
                    }
                }
                catch { }

                return new Model();
            }

            private void SaveNoLock()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch
                {
                    // best-effort
                }
            }
        }


        // ------------ ROOTS STORE (cartelle per Film/Video/Foto/Musica) ------------
        private sealed class RootsStore
        {
            private sealed class Model
            {
                public Dictionary<string, List<string>> Roots { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private Model _data;

            public RootsStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "libraryRoots.json");
                _data = Load();
            }

            // SEMPRE: restituisce path normalizzati (D:, D, D\ → D:\) e senza doppioni
            public List<string> Get(string category)
            {
                if (_data.Roots.TryGetValue(category, out var list))
                {
                    return list
                        .Select(p => MediaLibraryPage.NormalizeRootPath(p))
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                return new List<string>();
            }

            public void Set(string category, List<string> roots)
            {
                _data.Roots[category] = roots
                    .Select(MediaLibraryPage.NormalizeRootPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Save();
            }

            public void Add(string category, string root)
            {
                root = MediaLibraryPage.NormalizeRootPath(root);
                if (string.IsNullOrWhiteSpace(root))
                    return;

                if (!_data.Roots.TryGetValue(category, out var list))
                {
                    list = new List<string>();
                    _data.Roots[category] = list;
                }

                // niente doppioni, anche se in json c’era scritto "D:" e ora passi "D:\"
                if (!list.Any(p =>
                        string.Equals(MediaLibraryPage.NormalizeRootPath(p), root, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(root);
                    Save();
                }
            }

            public void Remove(string category, string root)
            {
                root = MediaLibraryPage.NormalizeRootPath(root);

                if (!_data.Roots.TryGetValue(category, out var list))
                    return;

                list.RemoveAll(p =>
                    string.Equals(MediaLibraryPage.NormalizeRootPath(p), root, StringComparison.OrdinalIgnoreCase));

                Save();
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null)
                        {
                            var normalized = new Model();
                            foreach (var kv in m.Roots)
                            {
                                var list = kv.Value
                                    .Select(p => MediaLibraryPage.NormalizeRootPath(p))
                                    .Where(p => !string.IsNullOrWhiteSpace(p))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();

                                if (list.Count > 0)
                                    normalized.Roots[kv.Key] = list;
                            }

                            return normalized;
                        }
                    }
                }
                catch { }
                return new Model();
            }

            private void Save()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch { }
            }
        }


        // ------------ LIBRARY INDEX STORE (tutto quello che abbiamo indicizzato per categoria) ------------
        private sealed class LibraryIndexStore
        {
            // modello molto semplice: categoria → lista di path
            private sealed class Model
            {
                public Dictionary<string, List<string>> Categories { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private readonly object _lock = new();
            private Model _data;

            public LibraryIndexStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "libraryIndex.json");
                _data = Load();
            }

            public List<string> GetPaths(string category)
            {
                lock (_lock)
                {
                    if (_data.Categories.TryGetValue(category, out var list))
                        return list.ToList();
                }
                return new List<string>();
            }

            public void ReplacePaths(string category, IEnumerable<string> paths)
            {
                lock (_lock)
                {
                    _data.Categories[category] = paths
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    SaveNoLock();
                }
            }

            public void RemoveMissing(string category)
            {
                lock (_lock)
                {
                    if (!_data.Categories.TryGetValue(category, out var list))
                        return;

                    var filtered = list
                        .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (filtered.Count != list.Count)
                    {
                        _data.Categories[category] = filtered;
                        SaveNoLock();
                    }
                }
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null) return m;
                    }
                }
                catch { }
                return new Model();
            }

            private void SaveNoLock()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch { }
            }
        }


        // ------------ DURATION INDEX STORE (cache persistente delle durate in minuti) ------------
        private sealed class DurationIndexStore
        {
            private sealed class Model
            {
                public Dictionary<string, double?> Items { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private readonly object _lock = new();
            private Model _data;

            public DurationIndexStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                // file separato dal libraryIndex esistente
                _file = Path.Combine(folder, "durationIndex.json");
                _data = Load();
            }

            public double? TryGet(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                lock (_lock)
                {
                    if (_data.Items.TryGetValue(path, out var v))
                        return v;
                }
                return null;
            }

            public void Set(string path, double? minutes)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                lock (_lock)
                {
                    _data.Items[path] = minutes;
                    SaveNoLock();
                }
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null) return m;
                    }
                }
                catch
                {
                    // se qualcosa va storto ripartiamo da un modello vuoto
                }

                return new Model();
            }

            private void SaveNoLock()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch
                {
                    // best-effort: se il salvataggio fallisce, pazienza.
                }
            }
        }


    }
}
