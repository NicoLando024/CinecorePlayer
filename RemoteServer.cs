// FILE: RemoteServer.cs — v8 TCP + fullscreen remote UI + PairingRequested + MAC bind
// - Server TCP Http-like (TcpListener) senza URLACL.
// - UI telecomando = fullscreen nero, niente finestrelle che si muovono.
// - Logo in alto a sinistra su sfondo nero, senza cerchietti/material.
// - Layout comandi:
//      HEADER (logo + titolo + renderer/HDR a sx, power a dx)
//      SEEK BAR
//      ROW1: [-10s] [PLAY] [+10s]
//      ROW2: [CAPIT PREV] [PAUSA] [CAPIT NEXT]
//      DPAD tondo
//      VOLUME con slider e PCM/BITSTREAM
//      GRIGLIA FUNZIONI EXTRA (Full / HDR / 3D / Libreria / Info / Impostazioni / STOP rosso)
//
// PATCH5 ADD:
// - Evento PairingRequested(pin) -> per far apparire il PIN sul player quando un device non è ancora abbinato
// - trusted.json persistente in %AppData%\CinecorePlayer2025
// - Salvataggio best-effort del MAC via ARP, riuso se stesso device si riabbina
// - 401 JSON arricchite { pair:true, pin:... } e RaisePairingRequested()
// - MAC salvato in TrustedToken
//
// #nullable enable
#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Linq;

internal sealed class RemoteServer : IDisposable
{
    // ====================== campi core ======================
    private readonly int _port;
    private readonly string _pin;
    private readonly Func<RemoteState> _getState;
    private readonly Action<string, Dictionary<string, string>> _handle;

    private TcpListener? _tcp;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private volatile bool _running;

    // ====================== trusted store ======================
    private readonly string _rootDir;
    private readonly string _storePath;
    private readonly object _lock = new();

    public event Action<string>? Paired;
    public event Action<string>? PairingRequested; // PIN corrente da mostrare nell'UI principale

    public int TrustedCount { get { lock (_lock) return _trusted.Count; } }
    public string CurrentPin => _pin;

    private sealed class TrustedToken
    {
        public string Token { get; set; } = "";
        public string? Name { get; set; }
        public string? LastIp { get; set; }
        public string? Mac { get; set; }          // NEW: MAC best-effort
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    private readonly List<TrustedToken> _trusted = new();

    // ====================== HTTP req/resp struct ======================
    private sealed class SimpleRequest
    {
        public string Method = "";
        public string Path = "";
        public string Query = "";
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Cookies = new(StringComparer.OrdinalIgnoreCase);
        public string Body = "";
        public string RemoteIp = "";
    }

    private sealed class SimpleResponse
    {
        public int StatusCode = 200;
        public string ContentType = "text/plain; charset=utf-8";
        public string BodyText = "";
        public List<(string Key, string Val)> ExtraHeaders = new();
    }

    // ====================== ctor ======================
    public RemoteServer(int port,
                        string? pin,
                        Func<RemoteState> getState,
                        Action<string, Dictionary<string, string>> handleCommand)
    {
        _port = port;
        _pin = string.IsNullOrWhiteSpace(pin) ? MakePin() : pin.Trim();
        _getState = getState;
        _handle = handleCommand;

        // PATCH5: path roaming + filename "trusted.json"
        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CinecorePlayer2025");
        Directory.CreateDirectory(_rootDir);

        _storePath = Path.Combine(_rootDir, "trusted.json");
        LoadTrusted();
    }

    private static string MakePin()
    {
        using var rng = RandomNumberGenerator.Create();
        Span<byte> b = stackalloc byte[4];
        rng.GetBytes(b);
        var n = BitConverter.ToUInt32(b) % 1_000_000u;
        return n.ToString("000000");
    }

    private void RaisePairingRequested()
    {
        try { PairingRequested?.Invoke(_pin); } catch { }
    }

    // ====================== start/stop ======================
    public void Start()
    {
        if (_running) return;

        _tcp = new TcpListener(IPAddress.Any, _port);
        try { _tcp.Start(); }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Impossibile avviare il server remoto sulla porta {_port}. " +
                $"Motivi tipici: firewall, porta occupata. Dettagli: {ex.Message}", ex);
        }

        _cts = new CancellationTokenSource();
        _running = true;
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Stop(); } catch { }
    }

    public void Dispose() => Stop();

    // ====================== loop accettazione ======================
    private async Task AcceptLoop(CancellationToken token)
    {
        if (_tcp == null) return;

        while (_running && !token.IsCancellationRequested)
        {
            TcpClient? cli = null;
            try
            {
                cli = await _tcp.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch
            {
                if (!_running) break;
                continue;
            }

            if (cli != null)
            {
                _ = Task.Run(() => HandleClient(cli));
            }
        }
    }

    // ====================== gestione client ======================
    private async Task HandleClient(TcpClient cli)
    {
        using (cli)
        using (var ns = cli.GetStream())
        using (var reader = new StreamReader(ns, Encoding.UTF8, false, 8192, true))
        using (var writer = new StreamWriter(ns, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true })
        {
            SimpleRequest? req = await ReadRequest(reader, cli);
            if (req == null) return;

            SimpleResponse resp;
            try
            {
                resp = ProcessRequest(req);
            }
            catch (Exception ex)
            {
                resp = JsonResp(new
                {
                    ok = false,
                    error = ex.GetType().Name,
                    message = ex.Message
                }, 500);
            }

            await WriteResponse(writer, resp);
        }
    }

    // ====================== parsing HTTP ======================
    private static async Task<SimpleRequest?> ReadRequest(StreamReader reader, TcpClient cli)
    {
        string? startLine = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(startLine)) return null;

        string[] parts = startLine.Split(' ');
        if (parts.Length < 2) return null;

        string method = parts[0].Trim().ToUpperInvariant();
        string urlPart = parts[1].Trim();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line == null) return null;
            if (line.Length == 0) break;

            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                string name = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                if (headers.TryGetValue(name, out var prev))
                    headers[name] = prev + ", " + value;
                else
                    headers[name] = value;
            }
        }

        string body = "";
        if (headers.TryGetValue("Content-Length", out var clStr)
            && int.TryParse(clStr, out int cl)
            && cl > 0)
        {
            var buf = new char[cl];
            int total = 0;
            while (total < cl)
            {
                int n = await reader.ReadAsync(buf, total, cl - total);
                if (n <= 0) break;
                total += n;
            }
            body = new string(buf, 0, total);
        }

        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers.TryGetValue("Cookie", out var cookieHeader))
        {
            var cookieParts = cookieHeader.Split(';');
            foreach (var cpart in cookieParts)
            {
                var cp = cpart.Trim();
                if (cp.Length == 0) continue;

                int eq = cp.IndexOf('=');
                if (eq >= 0)
                {
                    var cname = cp[..eq].Trim();
                    var cval = cp[(eq + 1)..].Trim();
                    if (!string.IsNullOrEmpty(cname))
                        cookies[cname] = cval;
                }
                else
                {
                    cookies[cp] = "";
                }
            }
        }

        string path;
        string query = "";
        int qm = urlPart.IndexOf('?');
        if (qm >= 0)
        {
            path = urlPart[..qm];
            query = urlPart[(qm + 1)..];
        }
        else
        {
            path = urlPart;
        }

        string remoteIp = ((IPEndPoint?)cli.Client.RemoteEndPoint)?.Address.ToString() ?? "?";

        return new SimpleRequest
        {
            Method = method,
            Path = path,
            Query = query,
            Headers = headers,
            Cookies = cookies,
            Body = body,
            RemoteIp = remoteIp
        };
    }

    // ====================== scrittura risposta ======================
    private static async Task WriteResponse(StreamWriter w, SimpleResponse resp)
    {
        string reason = ReasonPhrase(resp.StatusCode);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(resp.BodyText ?? "");

        await w.WriteLineAsync($"HTTP/1.1 {resp.StatusCode} {reason}");
        await w.WriteLineAsync("Access-Control-Allow-Origin: *");
        await w.WriteLineAsync("Access-Control-Allow-Headers: Content-Type, Authorization, X-Device-Name");
        await w.WriteLineAsync("Access-Control-Allow-Methods: GET,POST,DELETE,OPTIONS");
        await w.WriteLineAsync("Connection: close");
        await w.WriteLineAsync($"Content-Type: {resp.ContentType}");
        await w.WriteLineAsync($"Content-Length: {bodyBytes.Length}");

        foreach (var (k, v) in resp.ExtraHeaders)
            await w.WriteLineAsync($"{k}: {v}");

        await w.WriteLineAsync();
        await w.FlushAsync();

        await w.BaseStream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
        await w.BaseStream.FlushAsync();
    }

    private static string ReasonPhrase(int code) => code switch
    {
        200 => "OK",
        401 => "Unauthorized",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "OK"
    };

    // ====================== helpers risposta ======================
    private static SimpleResponse HtmlResp(string html) => new SimpleResponse
    {
        StatusCode = 200,
        ContentType = "text/html; charset=utf-8",
        BodyText = html
    };

    private static SimpleResponse JsonResp(object obj, int statusCode = 200) => new SimpleResponse
    {
        StatusCode = statusCode,
        ContentType = "application/json; charset=utf-8",
        BodyText = JsonSerializer.Serialize(obj)
    };

    private static void AddAuthCookie(SimpleResponse resp, string token)
    {
        resp.ExtraHeaders.Add((
            "Set-Cookie",
            $"ccp_token={token}; Path=/; HttpOnly; Max-Age=31536000"
        ));
    }

    private static void ExpireAuthCookie(SimpleResponse resp)
    {
        resp.ExtraHeaders.Add((
            "Set-Cookie",
            "ccp_token=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT"
        ));
    }

    private static string? ReadJsonPropFromBody(string body, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty(prop, out var v))
                return v.GetString();
        }
        catch { }
        return null;
    }

    // ====================== trusted management ======================
    private void LoadTrusted()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var json = File.ReadAllText(_storePath);

            // formato nuovo List<TrustedToken>
            var list = JsonSerializer.Deserialize<List<TrustedToken>>(json);
            if (list != null)
            {
                _trusted.Clear();
                _trusted.AddRange(list);
                return;
            }

            // fallback retrocompatibile array di string token vecchi
            var arr = JsonSerializer.Deserialize<string[]>(json);
            if (arr != null)
            {
                _trusted.Clear();
                foreach (var t in arr)
                {
                    _trusted.Add(new TrustedToken
                    {
                        Token = t,
                        FirstSeen = DateTime.UtcNow,
                        LastSeen = DateTime.UtcNow
                    });
                }
            }
        }
        catch
        {
        }
    }

    private void SaveTrusted()
    {
        try
        {
            var json = JsonSerializer.Serialize(_trusted,
                new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(_rootDir);
            File.WriteAllText(_storePath, json);
        }
        catch
        {
        }
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int phyAddrLen);

    private static string? TryGetMac(string ip)
    {
        try
        {
            var addr = IPAddress.Parse(ip);
            var bytes = addr.GetAddressBytes();
            int dest = BitConverter.ToInt32(bytes, 0);
            var mac = new byte[6];
            int len = mac.Length;
            if (SendARP(dest, 0, mac, ref len) == 0 && len == 6)
            {
                return string.Join(":", mac.Select(b => b.ToString("X2")));
            }
        }
        catch { }
        return null;
    }

    private TrustedToken CreateTrustedToken(string? devName, string remoteIp)
    {
        // PATCH5: prova a legare all'host tramite MAC (ARP)
        string? mac = TryGetMac(remoteIp);
        string newTokVal = NewToken();

        lock (_lock)
        {
            // se già conosciuto via MAC, aggiorna
            TrustedToken? existing = null;
            if (!string.IsNullOrWhiteSpace(mac))
            {
                existing = _trusted.Find(t => string.Equals(t.Mac, mac, StringComparison.OrdinalIgnoreCase));
            }

            if (existing == null)
            {
                var t = new TrustedToken
                {
                    Token = newTokVal,
                    Name = string.IsNullOrWhiteSpace(devName) ? null : devName,
                    FirstSeen = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                    LastIp = remoteIp,
                    Mac = mac
                };
                _trusted.Add(t);
                SaveTrusted();
                try { Paired?.Invoke(t.Name ?? t.Token); } catch { }
                return t;
            }
            else
            {
                // aggiorna device esistente per riuso stesso telefono/tablet
                existing.Token = newTokVal;
                if (!string.IsNullOrWhiteSpace(devName))
                    existing.Name = devName;
                existing.LastSeen = DateTime.UtcNow;
                existing.LastIp = remoteIp;
                if (!string.IsNullOrWhiteSpace(mac))
                    existing.Mac = mac;
                SaveTrusted();
                try { Paired?.Invoke(existing.Name ?? existing.Token); } catch { }
                return existing;
            }
        }
    }

    private bool IsAuthed(SimpleRequest req, out TrustedToken? tokObj)
    {
        tokObj = null;
        string? token = null;

        if (req.Cookies.TryGetValue("ccp_token", out var cookieTok)
            && !string.IsNullOrWhiteSpace(cookieTok))
        {
            token = cookieTok;
        }

        if (token == null &&
            req.Headers.TryGetValue("Authorization", out var auth) &&
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = auth.Substring(7).Trim();
        }

        if (string.IsNullOrEmpty(token))
            return false;

        lock (_lock)
        {
            tokObj = _trusted.Find(t => t.Token == token);
            if (tokObj != null)
            {
                tokObj.LastSeen = DateTime.UtcNow;
                tokObj.LastIp = req.RemoteIp;
                // Aggiorna MAC se riusciamo a sniffarla ora
                string? mac = TryGetMac(req.RemoteIp);
                if (!string.IsNullOrWhiteSpace(mac))
                    tokObj.Mac = mac;
                SaveTrusted();
                return true;
            }
        }

        return false;
    }

    // ====================== querystring parser ======================
    private static Dictionary<string, string> ParseQuery(string? q)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(q)) return dict;
        if (q.StartsWith("?")) q = q[1..];

        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
            var val = kv.Length > 1
                ? Uri.UnescapeDataString(kv[1].Replace('+', ' '))
                : "";
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
    }

    // ====================== routing ======================
    private SimpleResponse ProcessRequest(SimpleRequest req)
    {
        if (req.Method == "OPTIONS")
        {
            return new SimpleResponse
            {
                StatusCode = 200,
                ContentType = "text/plain; charset=utf-8",
                BodyText = ""
            };
        }

        if (req.Path == "/health")
        {
            return JsonResp(new { ok = true, online = true }, 200);
        }

        if (req.Path == "/" ||
            req.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            var qdict = ParseQuery(string.IsNullOrEmpty(req.Query) ? null : "?" + req.Query);

            // pairing via ?pin=
            if (qdict.TryGetValue("pin", out var pinQ) && pinQ == _pin)
            {
                req.Headers.TryGetValue("X-Device-Name", out var devName);
                if (!req.Headers.TryGetValue("User-Agent", out var ua)) ua = null;

                var newTok = CreateTrustedToken(devName ?? ua, req.RemoteIp);
                var respOk = HtmlResp(RemoteHtml());
                AddAuthCookie(respOk, newTok.Token);
                return respOk;
            }

            // già autenticato -> telecomando
            if (IsAuthed(req, out _))
            {
                return HtmlResp(RemoteHtml());
            }

            // NON autenticato -> pagina PIN e alzo PairingRequested
            RaisePairingRequested();
            return HtmlResp(PinHtml());
        }

        // POST /api/auth
        if (req.Path == "/api/auth" && req.Method == "POST")
        {
            var pinBody = ReadJsonPropFromBody(req.Body, "pin");
            if (pinBody == _pin)
            {
                var nameBody = ReadJsonPropFromBody(req.Body, "name");
                if (!req.Headers.TryGetValue("User-Agent", out var ua)) ua = null;
                var newTok = CreateTrustedToken(
                    string.IsNullOrWhiteSpace(nameBody) ? ua : nameBody,
                    req.RemoteIp);

                var okResp = JsonResp(new { ok = true, token = newTok.Token }, 200);
                AddAuthCookie(okResp, newTok.Token);
                return okResp;
            }

            // PIN errato
            RaisePairingRequested();
            return JsonResp(new { ok = false, error = "bad pin", pair = true, pin = _pin }, 401);
        }

        // POST /api/logout
        if (req.Path == "/api/logout" && req.Method == "POST")
        {
            if (IsAuthed(req, out var tok) && tok != null)
            {
                lock (_lock)
                {
                    _trusted.RemoveAll(x => x.Token == tok.Token);
                    SaveTrusted();
                }
            }

            var outResp = JsonResp(new { ok = true }, 200);
            ExpireAuthCookie(outResp);
            return outResp;
        }

        // GET /api/trusted
        if (req.Path == "/api/trusted" && req.Method == "GET")
        {
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
            }

            List<object> view;
            lock (_lock)
            {
                view = new List<object>(_trusted.Count);
                foreach (var t in _trusted)
                {
                    string shortTok = t.Token.Length > 6 ? t.Token[..6] + "…" : t.Token;
                    view.Add(new
                    {
                        token = shortTok,
                        name = t.Name,
                        lastIp = t.LastIp,
                        mac = t.Mac,
                        firstSeen = t.FirstSeen,
                        lastSeen = t.LastSeen
                    });
                }
            }
            return JsonResp(new { ok = true, devices = view }, 200);
        }

        // POST /api/trusted/rename
        if (req.Path == "/api/trusted/rename" && req.Method == "POST")
        {
            if (!IsAuthed(req, out var tok) || tok == null)
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
            }

            var newName = ReadJsonPropFromBody(req.Body, "name");
            lock (_lock)
            {
                tok.Name = string.IsNullOrWhiteSpace(newName) ? null : newName;
                SaveTrusted();
            }

            return JsonResp(new { ok = true }, 200);
        }

        // DELETE /api/trusted
        if (req.Path == "/api/trusted" && req.Method == "DELETE")
        {
            if (!IsAuthed(req, out var tok) || tok == null)
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
            }

            lock (_lock)
            {
                _trusted.RemoveAll(x => x.Token == tok.Token);
                SaveTrusted();
            }

            var delResp = JsonResp(new { ok = true }, 200);
            ExpireAuthCookie(delResp);
            return delResp;
        }

        // GET /api/state
        if (req.Path == "/api/state")
        {
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
            }

            var st = _getState();
            return JsonResp(st, 200);
        }

        // GET /api/cmd
        if (req.Path == "/api/cmd")
        {
            if (!IsAuthed(req, out _))
            {
                RaisePairingRequested();
                return JsonResp(new { ok = false, error = "pin required", pair = true, pin = _pin }, 401);
            }

            var q = ParseQuery(string.IsNullOrEmpty(req.Query) ? null : "?" + req.Query);
            string cmd = q.TryGetValue("cmd", out var c) ? c : "";
            _handle(cmd, q);

            return JsonResp(new { ok = true }, 200);
        }

        // not found
        return JsonResp(new { ok = false, error = "not found" }, 404);
    }

    // ====================== pagina PIN ======================
    private static string PinHtml() => @"<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no'>
<title>Cinecore Remote – PIN</title>
<style>
:root{
  --bg:#000;
  --txt:#fff;
  --dim:#8a8a8a;
  --border:#2a2a2a;
  --danger:#ff4f6a;
  --btn:#1a1a1a;
  --btnborder:#3a3a3a;
  --font:system-ui,'Segoe UI',Roboto,Arial,sans-serif;
}
*{box-sizing:border-box;margin:0;padding:0;-webkit-tap-highlight-color:transparent}
body{
  background:#000;
  color:var(--txt);
  font-family:var(--font);
  min-height:100vh;
  padding:24px 16px 32px;
  display:flex;
  align-items:flex-start;
  justify-content:center;
}
.wrap{
  width:min(400px,100%);
  display:flex;
  flex-direction:column;
  gap:20px;
}
.hdr{
  text-align:center;
}
.logo-row{
  display:flex;
  justify-content:center;
  align-items:center;
  gap:8px;
  font-size:14px;
  font-weight:600;
  color:#fff;
  text-transform:uppercase;
  letter-spacing:.05em;
}
.subtitle{
  font-size:13px;
  color:var(--dim);
  line-height:1.4;
  margin-top:4px;
}
.pinbox{
  border-top:1px solid var(--border);
  padding-top:20px;
  display:flex;
  flex-direction:column;
  gap:14px;
}
input{
  width:100%;
  font-size:24px;
  padding:14px 12px;
  background:#0c0c0c;
  color:#fff;
  border-radius:10px;
  border:1px solid var(--border);
  text-align:center;
  letter-spacing:.35em;
  font-weight:600;
  outline:none;
}
.keypad{
  display:grid;
  grid-template-columns:repeat(3,1fr);
  gap:10px;
}
.keypad button,
#go{
  appearance:none;
  background:var(--btn);
  border:1px solid var(--btnborder);
  border-radius:10px;
  color:#fff;
  font-size:18px;
  font-weight:600;
  padding:14px 0;
}
#go{
  width:100%;
  font-size:15px;
  text-transform:uppercase;
  letter-spacing:.04em;
}
.keypad button:active,#go:active{
  transform:translateY(1px);
}
.err{
  min-height:20px;
  font-size:13px;
  font-weight:500;
  color:var(--danger);
  text-align:center;
}
.note{
  font-size:12px;
  text-align:center;
  color:var(--dim);
  line-height:1.4;
}
</style>
</head>
<body>
<div class='wrap'>
  <div class='hdr'>
    <div class='logo-row'>
      <span>CinecorePlayer2025</span>
      <span>•</span>
      <span>Remote Pair</span>
    </div>
    <div class='subtitle'>Guarda il PIN sul player e inseriscilo qui. Dopo l'abbinamento questo dispositivo resta autorizzato.</div>
  </div>

  <div class='pinbox'>
    <input id='pin' inputmode='numeric' pattern='[0-9]*' maxlength='8' autofocus placeholder='••••••'>
    <div class='keypad'>
      <button data-d='1'>1</button><button data-d='2'>2</button><button data-d='3'>3</button>
      <button data-d='4'>4</button><button data-d='5'>5</button><button data-d='6'>6</button>
      <button data-d='7'>7</button><button data-d='8'>8</button><button data-d='9'>9</button>
      <button data-d='clr'>C</button><button data-d='0'>0</button><button data-d='del'>⌫</button>
    </div>
    <button id='go'>Abbina</button>
    <div class='err' id='err'></div>
    <div class='note'>Se esci senza abbinarlo il player continua a mostrare il PIN.</div>
  </div>
</div>
<script>
const pin=document.getElementById('pin');
const err=document.getElementById('err');

document.querySelectorAll('.keypad button').forEach(b=>{
  b.onclick=()=>{
    const d=b.dataset.d;
    if(d==='clr') pin.value='';
    else if(d==='del') pin.value=pin.value.slice(0,-1);
    else pin.value+=d;
    pin.focus();
  };
});

async function auth(p, name){
  err.textContent='';
  const r=await fetch('/api/auth',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({pin:p, name:name||navigator.userAgent})
  });
  if(r.ok){
    location.href='/';
  }else{
    err.textContent='PIN errato';
  }
}
document.getElementById('go').onclick=()=>auth(pin.value.trim(), navigator.userAgent);
pin.addEventListener('keydown',e=>{
  if(e.key==='Enter') document.getElementById('go').click();
});
</script>
</body>
</html>";

    // ====================== telecomando fullscreen ======================
    private static string RemoteHtml()
    {
        // Provo logo bianco in Assets/logo.png, fallback testo SVG bianco.
        string logoFallbackSvgEscaped = Uri.EscapeDataString(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 80'>" +
            "<text x='0' y='55' font-size='48' font-family='Segoe UI,Roboto,Arial' font-weight='700' fill='#fff'>CinecorePlayer2025</text>" +
            "</svg>"
        );

        string logoDataUri;
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(p))
            {
                var bytes = File.ReadAllBytes(p);
                logoDataUri = "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
            else
            {
                logoDataUri = "data:image/svg+xml;utf8," + logoFallbackSvgEscaped;
            }
        }
        catch
        {
            logoDataUri = "data:image/svg+xml;utf8," + logoFallbackSvgEscaped;
        }

        return @"<!doctype html>
<html lang='it'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no'>
<title>Cinecore Remote</title>
<style>
:root{
  --bg:#000;
  --txt:#fff;
  --dim:#7a7a7a;
  --line:#2a2a2a;
  --panel:#0f0f0f;
  --panel2:#1a1a1a;
  --border:#2e2e2e;
  --border-soft:#3a3a3a;
  --accent:#39a8ff;
  --accent2:#8a6bff;
  --warn:#ff4f6a;
  --font:system-ui,'Segoe UI',Roboto,Arial,sans-serif;
  --btn-radius:12px;
}
*{box-sizing:border-box;margin:0;padding:0;-webkit-tap-highlight-color:transparent}
html,body{
  background:var(--bg);
  color:var(--txt);
  font-family:var(--font);
  min-height:100%;
  height:100%;
}
body{
  display:flex;
  flex-direction:column;
  padding:16px 16px 32px;
}
.section{
  padding:16px 0;
  border-bottom:1px solid var(--line);
}
.section:first-of-type{
  padding-top:0;
}
.row-spread{
  display:flex;
  justify-content:space-between;
  align-items:flex-start;
  gap:12px;
}

/* HEADER */
.header-left{
  min-width:0;
  display:flex;
  flex-direction:row;
  gap:12px;
  align-items:flex-start;
}
.logo-wrap{
  display:flex;
  flex-direction:column;
  gap:4px;
}
.logo-img{
  max-height:24px;
  height:24px;
  width:auto;
  object-fit:contain;
}
.media-info{
  min-width:0;
  display:flex;
  flex-direction:column;
  line-height:1.3;
}
.media-title{
  font-size:14px;
  font-weight:600;
  color:var(--txt);
  white-space:nowrap;
  overflow:hidden;
  text-overflow:ellipsis;
  max-width:220px;
}
.media-meta{
  font-size:12px;
  color:var(--dim);
  white-space:nowrap;
  overflow:hidden;
  text-overflow:ellipsis;
  max-width:220px;
  margin-top:2px;
}
.power-btn{
  appearance:none;
  background:radial-gradient(circle at 50% 20%,#c00 0%,#600 70%);
  border:1px solid #f44;
  border-radius:50%;
  width:40px;
  height:40px;
  font-size:15px;
  color:#fff;
  font-weight:600;
  line-height:40px;
  text-align:center;
  box-shadow:0 0 20px rgba(255,0,0,.6),0 10px 30px rgba(0,0,0,.9);
}
.power-btn:active{
  transform:scale(.96);
}

/* SEEK */
.seek-wrap{
  display:flex;
  flex-direction:column;
  gap:8px;
}
input[type=range]{
  -webkit-appearance:none;
  appearance:none;
  width:100%;
  background:transparent;
  margin:0;
  height:32px;
}
input[type=range]::-webkit-slider-runnable-track{
  height:6px;
  background:linear-gradient(90deg,var(--accent) 0%,var(--accent2) 100%);
  border-radius:4px;
  box-shadow:inset 0 0 0 1px rgba(255,255,255,.08),
             0 0 12px rgba(57,168,255,.5);
}
input[type=range]::-moz-range-track{
  height:6px;
  background:linear-gradient(90deg,var(--accent) 0%,var(--accent2) 100%);
  border-radius:4px;
  border:none;
}
input[type=range]::-webkit-slider-thumb{
  -webkit-appearance:none;
  appearance:none;
  width:20px;
  height:20px;
  border-radius:50%;
  background:#fff;
  border:1px solid #c9c9c9;
  box-shadow:0 2px 4px rgba(0,0,0,.7),
             0 0 10px rgba(255,255,255,.6);
  margin-top:-7px;
}
input[type=range]::-moz-range-thumb{
  width:20px;
  height:20px;
  border-radius:50%;
  background:#fff;
  border:1px solid #c9c9c9;
}
.time-row{
  display:flex;
  justify-content:space-between;
  font-family:monospace;
  font-size:11px;
  font-weight:500;
  color:var(--dim);
}

/* PLAYBACK CONTROLS */
.play-block{
  display:flex;
  flex-direction:column;
  gap:12px;
}
.play-row{
  display:grid;
  grid-template-columns:repeat(3,1fr);
  gap:12px;
}
.pbtn{
  appearance:none;
  background:var(--panel2);
  border:1px solid var(--border-soft);
  border-radius:var(--btn-radius);
  color:var(--txt);
  text-align:center;
  padding:10px 4px;
  min-height:64px;
  box-shadow:0 0 20px rgba(0,0,0,.7);
}
.pbtn.main{
  background:radial-gradient(circle at 50% 20%,#3a3a3a 0%,#101010 70%);
  border:1px solid var(--border);
}
.pbtn:active{
  transform:translateY(1px);
}
.pbtn-inner{
  display:flex;
  flex-direction:column;
  justify-content:center;
  align-items:center;
  gap:4px;
  line-height:1.2;
}
.pbtn-inner .ico{
  font-size:18px;
  font-weight:600;
  color:#fff;
}
.pbtn-inner .label{
  font-size:12px;
  font-weight:600;
  color:#fff;
  opacity:.9;
  white-space:nowrap;
}

/* DPAD */
.dpad-outer{
  display:flex;
  justify-content:center;
  align-items:center;
}
.dpad{
  position:relative;
  width:150px;
  height:150px;
  border-radius:50%;
  background:radial-gradient(circle at 50% 30%,#1f1f1f 0%,#0c0c0c 70%);
  border:1px solid var(--border);
  box-shadow:0 20px 40px rgba(0,0,0,.9),0 0 20px rgba(0,0,0,.8) inset;
}
.dpad button{
  position:absolute;
  appearance:none;
  background:var(--panel2);
  border:1px solid var(--border-soft);
  color:var(--txt);
  font-size:16px;
  font-weight:600;
  line-height:1;
  width:44px;
  height:44px;
  border-radius:10px;
  box-shadow:0 10px 20px rgba(0,0,0,.8),0 1px 2px rgba(255,255,255,.07) inset;
}
.dpad button:active{
  transform:scale(.96);
}
.dpad-up   {top:10px; left:50%; transform:translateX(-50%);}
.dpad-down {bottom:10px; left:50%; transform:translateX(-50%);}
.dpad-left {left:10px; top:50%; transform:translateY(-50%);}
.dpad-right{right:10px; top:50%; transform:translateY(-50%);}
.dpad-ok{
  top:50%; left:50%; transform:translate(-50%,-50%);
  width:60px; height:60px; border-radius:12px;
  font-size:15px;
  background:radial-gradient(circle at 50% 20%,#2a2a2a 0%,#0f0f0f 70%);
  border:1px solid var(--border);
}

/* VOLUME */
.vol-wrap{
  display:flex;
  flex-direction:column;
  gap:14px;
}
.vline1{
  display:grid;
  grid-template-columns:auto 1fr auto;
  gap:12px;
  align-items:center;
}
.vol-sidebtn{
  width:40px;
  height:40px;
  border-radius:10px;
  appearance:none;
  background:var(--panel2);
  border:1px solid var(--border-soft);
  box-shadow:0 10px 20px rgba(0,0,0,.8),0 1px 2px rgba(255,255,255,.07) inset;
  font-size:16px;
  font-weight:600;
  color:var(--txt);
  line-height:40px;
  text-align:center;
}
.vol-sidebtn:active{ transform:scale(.96); }
.vol-mid{
  display:flex;
  flex-direction:column;
  gap:4px;
}
.vol-label-line{
  font-size:12px;
  font-weight:500;
  color:var(--dim);
  display:flex;
  gap:8px;
  align-items:center;
}
.audio-mode{
  font-size:11px;
  line-height:1.2;
  font-weight:600;
  color:#fff;
  background:var(--panel2);
  border:1px solid var(--border-soft);
  border-radius:999px;
  padding:3px 8px;
  min-width:70px;
  text-align:center;
  box-shadow:0 10px 20px rgba(0,0,0,.8),0 1px 2px rgba(255,255,255,.07) inset;
}
.vol-slider{
  grid-column:1 / span 3;
}
.vol-slider input[type=range]::-webkit-slider-runnable-track{
  background:linear-gradient(90deg,var(--accent) 0%,var(--accent2) 100%);
}

/* EXTRA GRID */
.extra-grid{
  display:grid;
  grid-template-columns:repeat(3,1fr);
  gap:12px;
}
.exbtn{
  appearance:none;
  background:var(--panel2);
  border:1px solid var(--border-soft);
  border-radius:var(--btn-radius);
  min-height:60px;
  color:#fff;
  text-align:center;
  padding:10px 4px;
  line-height:1.2;
  box-shadow:0 0 20px rgba(0,0,0,.7);
  display:flex;
  flex-direction:column;
  justify-content:center;
  align-items:center;
  gap:4px;
}
.exbtn .ico{
  font-size:15px;
  font-weight:600;
  line-height:1;
}
.exbtn .label{
  font-size:12px;
  font-weight:600;
  opacity:.9;
}
.exbtn.warn{
  grid-column:1 / span 3;
  background:radial-gradient(circle at 50% 20%,#c00 0%,#400 70%);
  border:1px solid #f44;
  box-shadow:0 0 20px rgba(255,0,0,.6),0 20px 40px rgba(0,0,0,.9);
}
.exbtn:active{
  transform:translateY(1px);
}
.netrow{
  font-size:11px;
  color:var(--dim);
  text-align:center;
  padding-top:8px;
  line-height:1.4;
  word-break:break-word;
}
</style>
</head>
<body>

<!-- HEADER -->
<section class='section'>
  <div class='row-spread'>
    <div class='header-left'>
      <div class='logo-wrap'>
        <img class='logo-img' src='" + logoDataUri + @"' alt='logo'>
      </div>
      <div class='media-info'>
        <div class='media-title' id='title'>—</div>
        <div class='media-meta'><span id='renderer'>—</span> · <span id='hdr'>—</span></div>
      </div>
    </div>
    <button class='power-btn' onclick='cmd(""stop"")'>⏻</button>
  </div>
</section>

<!-- SEEK -->
<section class='section'>
  <div class='seek-wrap'>
    <input id='seek' type='range' min='0' max='0' value='0' step='0.1'
           oninput='onSeekInput(this.value)'
           onchange='onSeekCommit(this.value)' />
    <div class='time-row'>
      <span id='tcur'>00:00</span>
      <span id='tdur'>00:00</span>
    </div>
  </div>
</section>

<!-- PLAYBACK CONTROLS -->
<section class='section'>
  <div class='play-block'>

    <!-- Riga 1: -10s | Play | +10s -->
    <div class='play-row'>

      <button class='pbtn' onclick='cmd(""back10"")'>
        <div class='pbtn-inner'>
          <div class='ico'>⟲10</div>
          <div class='label'>-10s</div>
        </div>
      </button>

      <button class='pbtn main' id='playBtn' onclick='cmd(""play"")'>
        <div class='pbtn-inner'>
          <div class='ico' id='playIco'>▶</div>
          <div class='label' id='playLbl'>Play</div>
        </div>
      </button>

      <button class='pbtn' onclick='cmd(""fwd10"")'>
        <div class='pbtn-inner'>
          <div class='ico'>10⟳</div>
          <div class='label'>+10s</div>
        </div>
      </button>

    </div>

    <!-- Riga 2: PrevCap | Pausa | NextCap -->
    <div class='play-row'>

      <button class='pbtn' onclick='cmd(""prev"")'>
        <div class='pbtn-inner'>
          <div class='ico'>⏮</div>
          <div class='label'>Capitolo -</div>
        </div>
      </button>

      <button class='pbtn main' id='pauseBtn' onclick='cmd(""pause"")'>
        <div class='pbtn-inner'>
          <div class='ico' id='pauseIco'>⏸</div>
          <div class='label' id='pauseLbl'>Pausa</div>
        </div>
      </button>

      <button class='pbtn' onclick='cmd(""next"")'>
        <div class='pbtn-inner'>
          <div class='ico'>⏭</div>
          <div class='label'>Capitolo +</div>
        </div>
      </button>

    </div>

  </div>
</section>

<!-- DPAD -->
<section class='section'>
  <div class='dpad-outer'>
    <div class='dpad'>
      <button class='dpad-up'    onclick='cmd(""up"")'>▲</button>
      <button class='dpad-down'  onclick='cmd(""down"")'>▼</button>
      <button class='dpad-left'  onclick='cmd(""left"")'>◀</button>
      <button class='dpad-right' onclick='cmd(""right"")'>▶</button>
      <button class='dpad-ok'    onclick='cmd(""ok"")'>OK</button>
    </div>
  </div>
</section>

<!-- VOLUME -->
<section class='section'>
  <div class='vol-wrap'>

    <div class='vline1'>
      <button class='vol-sidebtn' onclick='cmd(""voldown"")'>–</button>

      <div class='vol-mid'>
        <div class='vol-label-line'>
          <span>Volume</span>
          <span class='audio-mode' id='bitstream' title='Se BITSTREAM, volume fisso 100%'>PCM</span>
        </div>
      </div>

      <button class='vol-sidebtn' onclick='cmd(""volup"")'>+</button>

      <div class='vol-slider'>
        <input id='vol' type='range' min='0' max='1' step='0.01' value='1' oninput='onVol(this.value)' />
      </div>
    </div>

  </div>
</section>

<!-- EXTRA -->
<section class='section'>
  <div class='extra-grid'>

    <button class='exbtn' onclick='cmd(""full"")'>
      <div class='ico'>⛶</div>
      <div class='label'>Full</div>
    </button>

    <button class='exbtn' onclick='cmd(""hdr"")'>
      <div class='ico'>HDR</div>
      <div class='label'>HDR / SDR</div>
    </button>

    <button class='exbtn' onclick='cmd(""stereo"")'>
      <div class='ico'>3D</div>
      <div class='label'>3D / 2D</div>
    </button>

    <button class='exbtn' onclick='cmd(""library"")'>
      <div class='ico'>≡</div>
      <div class='label'>Libreria</div>
    </button>

    <button class='exbtn' onclick='cmd(""info"")'>
      <div class='ico'>ⓘ</div>
      <div class='label'>Info</div>
    </button>

    <button class='exbtn' onclick='cmd(""settings"")'>
      <div class='ico'>⚙</div>
      <div class='label'>Impost.</div>
    </button>

    <button class='exbtn warn' onclick='cmd(""stop"")'>
      <div class='ico'>■</div>
      <div class='label'>STOP</div>
    </button>

  </div>

  <div class='netrow' id='net'>—</div>
</section>

<script>
let last = {Duration:0, Position:0, Playing:false, Volume:1, Bitstream:false};

function fmt(sec){
  sec = Math.max(0, Math.floor(sec || 0));
  let h = Math.floor(sec/3600);
  let m = Math.floor((sec%3600)/60);
  let s = sec%60;
  return (h>0? (''+h).padStart(2,'0')+':' : '')
        +(''+m).padStart(2,'0')+':'
        +(''+s).padStart(2,'0');
}
function qs(sel){ return document.querySelector(sel); }

function poll(){
  fetch('/api/state')
    .then(r=>r.json())
    .then(s=>{
      last = s;

      qs('#title').textContent    = s.Title || '—';
      qs('#renderer').textContent = s.Renderer || '—';
      qs('#hdr').textContent      = s.HdrMode || (s.FileHdr? 'HDR' : 'SDR');
      qs('#bitstream').textContent= s.Bitstream? 'BITSTREAM' : 'PCM';

      // seek sync
      if(s.Duration>0){
        let seek = qs('#seek');
        if(!seek._drag){
          seek.max   = s.Duration.toFixed(1);
          seek.value = s.Position.toFixed(1);
          qs('#tcur').textContent = fmt(s.Position);
          qs('#tdur').textContent = fmt(s.Duration);
        }
      }else{
        qs('#seek').max='0';
        qs('#seek').value='0';
        qs('#tcur').textContent='00:00';
        qs('#tdur').textContent='00:00';
      }

      // volume sync
      if(!s.Bitstream && !qs('#vol')._drag){
        qs('#vol').value = (s.Volume ?? 1);
      }

      // rete/bitrate
      qs('#net').textContent =
        (s.AudioNowKbps>0? ('Audio '+s.AudioNowKbps+' kbps'):'') +
        (s.VideoNowKbps>0? (' • Video '+s.VideoNowKbps+' kbps'):'');
    })
    .catch(_=>{});
}
setInterval(poll,700);
poll();

function cmd(c){
  fetch('/api/cmd?cmd='+encodeURIComponent(c)).catch(()=>{});
}

function onSeekInput(v){
  let e = qs('#seek');
  e._drag = true;
  qs('#tcur').textContent = fmt(parseFloat(v||'0'));
}
function onSeekCommit(v){
  let e = qs('#seek');
  e._drag = false;
  fetch('/api/cmd?cmd=seek&pos='+encodeURIComponent(v)).catch(()=>{});
}

let volT=null;
function onVol(v){
  let e = qs('#vol');
  e._drag = true;
  clearTimeout(volT);
  volT = setTimeout(()=>{
    e._drag=false;
    fetch('/api/cmd?cmd=vol&v='+encodeURIComponent(v)).catch(()=>{});
  },120);
}
</script>
</body>
</html>";
    }

    // ====================== util rete ======================
    public static string[] LocalIPv4List()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var props = ni.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(ua.Address.ToString());
            }
        }
        return list.ToArray();
    }
}

// ====================== stato player esposto su /api/state ======================
internal sealed class RemoteState
{
    public string Title { get; set; } = "";
    public bool Playing { get; set; }
    public double Position { get; set; }
    public double Duration { get; set; }
    public float Volume { get; set; }
    public bool HasVideo { get; set; }
    public bool Bitstream { get; set; }
    public string Renderer { get; set; } = "";
    public int AudioNowKbps { get; set; }
    public int VideoNowKbps { get; set; }

    public bool FileHdr { get; set; }
    public string HdrMode { get; set; } = "";
}
