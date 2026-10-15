// =====================================================================
//  CR2 -> JPEG Converter  (lightweight version, single file)
//
//  How it works: every Canon CR2 already contains a full-size JPEG that
//  the camera saved while shooting. This app copies that JPEG out and
//  adds back the rotation, camera name and date. No RAW decoding, so it
//  is very fast, uses almost no memory, and cannot produce broken images.
//
//  No NuGet packages needed.
//  Run with:  dotnet run --urls http://localhost:5055
// =====================================================================
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = long.MaxValue);

var app = builder.Build();

// Keep the rest of the PC responsive while converting
try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

var uploadOut = Path.Combine(Directory.GetCurrentDirectory(), "converted");
Directory.CreateDirectory(uploadOut);
var jobs = new ConcurrentDictionary<string, Job>();

app.MapGet("/", () => Results.Content(Html.Page, "text/html; charset=utf-8"));

// ---------------------------------------------------------------------
//  Upload mode: one file per request
// ---------------------------------------------------------------------
app.MapPost("/api/upload", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null) return Results.BadRequest(new { error = "No file received." });

    var outName = Path.GetFileNameWithoutExtension(Path.GetFileName(file.FileName)) + ".jpg";
    var dst = Path.Combine(uploadOut, outName);

    Stream src = file.OpenReadStream();
    string? tmp = null;
    if (!src.CanSeek)
    {
        tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cr2");
        await using (var t = File.Create(tmp)) await src.CopyToAsync(t);
        await src.DisposeAsync();
        src = File.OpenRead(tmp);
    }

    try
    {
        var info = await Task.Run(() => Cr2.Extract(src, dst, DateTime.Now));
        return Results.Json(new
        {
            name = outName,
            width = info.Width,
            height = info.Height,
            size = info.Bytes,
            warning = info.Warning,
            url = "/api/file/" + Uri.EscapeDataString(outName)
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    finally
    {
        await src.DisposeAsync();
        if (tmp != null) File.Delete(tmp);
    }
});

app.MapGet("/api/file/{name}", (string name, bool? download) =>
{
    var path = Path.Combine(uploadOut, Path.GetFileName(name));
    if (!File.Exists(path)) return Results.NotFound();
    return download == true
        ? Results.File(path, "image/jpeg", Path.GetFileName(name))
        : Results.File(path, "image/jpeg");
});

// ---------------------------------------------------------------------
//  Folder mode: convert everything inside a folder, one file at a time
// ---------------------------------------------------------------------
app.MapPost("/api/folder/start", (FolderRequest r) =>
{
    var input = (r.Input ?? "").Trim().Trim('"');
    if (input.Length == 0 || !Directory.Exists(input))
        return Results.BadRequest(new { error = "Input folder was not found. Check the path." });

    var output = string.IsNullOrWhiteSpace(r.Output) ? Path.Combine(input, "jpeg") : r.Output.Trim().Trim('"');
    Directory.CreateDirectory(output);

    var files = Directory
        .EnumerateFiles(input, "*.cr2", new EnumerationOptions
        {
            RecurseSubdirectories = r.Recursive,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        })
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (files.Count == 0)
        return Results.BadRequest(new { error = "No .CR2 files found in that folder." });

    var job = new Job { Id = Guid.NewGuid().ToString("N"), Total = files.Count, Output = output };
    jobs[job.Id] = job;

    _ = Task.Run(() =>
    {
        try
        {
            foreach (var src in files)
            {
                job.Cts.Token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(src);
                job.Current = name;
                try
                {
                    var rel = Path.GetRelativePath(input, src);
                    var dst = Path.ChangeExtension(Path.Combine(output, rel), ".jpg");
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                    if (r.SkipExisting && File.Exists(dst))
                    {
                        job.Skipped++;
                    }
                    else
                    {
                        using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
                        var info = Cr2.Extract(fs, dst, File.GetLastWriteTime(src));
                        job.OutBytes += info.Bytes;
                        if (info.Warning != null)
                        {
                            job.Warned++;
                            job.Notes.Enqueue(name + ": " + info.Warning);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    job.Failed++;
                    job.Notes.Enqueue(name + ": " + ex.Message);
                }
                finally
                {
                    job.Done++;
                }
            }
        }
        catch (OperationCanceledException) { job.Cancelled = true; }
        finally { job.Finished = true; }
    });

    return Results.Json(new { id = job.Id });
});

app.MapGet("/api/folder/status/{id}", (string id) =>
{
    if (!jobs.TryGetValue(id, out var j)) return Results.NotFound();
    return Results.Json(new
    {
        total = j.Total,
        done = j.Done,
        failed = j.Failed,
        skipped = j.Skipped,
        warned = j.Warned,
        outBytes = j.OutBytes,
        current = j.Current,
        finished = j.Finished,
        cancelled = j.Cancelled,
        output = j.Output,
        notes = j.Notes.TakeLast(15)
    });
});

app.MapPost("/api/folder/cancel/{id}", (string id) =>
{
    if (jobs.TryGetValue(id, out var j)) j.Cts.Cancel();
    return Results.Ok();
});

app.Run();

// ---------------------------------------------------------------------
//  Types
// ---------------------------------------------------------------------
record FolderRequest(string? Input, string? Output, bool Recursive, bool SkipExisting);
record Info(int Width, int Height, long Bytes, string? Warning);

class Job
{
    public string Id = "";
    public int Total;
    public int Done;
    public int Failed;
    public int Skipped;
    public int Warned;
    public long OutBytes;
    public string Current = "";
    public string Output = "";
    public volatile bool Finished;
    public volatile bool Cancelled;
    public ConcurrentQueue<string> Notes = new();
    public CancellationTokenSource Cts = new();
}

// ---------------------------------------------------------------------
//  CR2 reader: finds the biggest JPEG inside the file and copies it out
// ---------------------------------------------------------------------
static class Cr2
{
    sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);

    static readonly int[] TypeSize = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8 };

    static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    static void W16(Stream s, ushort v) { Span<byte> t = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, v); s.Write(t); }
    static void W32(Stream s, uint v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); s.Write(t); }

    static byte[] ReadAt(Stream s, long pos, int len)
    {
        if (pos < 0 || len < 0 || pos + len > s.Length)
            throw new InvalidDataException("This file is cut off or damaged. It may not have copied fully from the card.");
        var buf = new byte[len];
        s.Seek(pos, SeekOrigin.Begin);
        s.ReadExactly(buf);
        return buf;
    }

    static (List<Entry> Entries, uint Next) ReadIfd(Stream s, long off)
    {
        int n = U16(ReadAt(s, off, 2), 0);
        if (n == 0 || n > 300) throw new InvalidDataException("Unreadable CR2 structure.");
        var table = ReadAt(s, off + 2, n * 12 + 4);
        var list = new List<Entry>(n);
        for (int k = 0; k < n; k++)
        {
            int e = k * 12;
            ushort tag = U16(table, e), type = U16(table, e + 2);
            uint count = U32(table, e + 4);
            long size = (long)(type < TypeSize.Length ? TypeSize[type] : 1) * count;
            byte[] data;
            if (size <= 4) data = table.AsSpan(e + 8, (int)size).ToArray();
            else if (size > 65536) data = Array.Empty<byte>();          // too big to copy (not needed)
            else data = ReadAt(s, U32(table, e + 8), (int)size);
            list.Add(new Entry(tag, type, count, data));
        }
        return (list, U32(table, n * 12));
    }

    static Entry? Find(List<Entry> l, ushort tag) => l.Find(x => x.Tag == tag);

    static uint Num(Entry? e) =>
        e is null || e.Data.Length < 2 ? 0 : e.Data.Length >= 4 ? U32(e.Data, 0) : U16(e.Data, 0);

    // Builds a small EXIF block (rotation, camera, date, exposure info)
    static byte[]? BuildExifSegment(List<Entry> ifd0, List<Entry> exif)
    {
        var keep0 = ifd0.Where(e => (e.Tag is 0x10F or 0x110 or 0x112 or 0x132 or 0x13B or 0x8298)
                                    && e.Data.Length > 0 && !(e.Type == 2 && e.Count <= 1)).ToList();
        var keep1 = exif.Where(e => e.Tag != 0x927C && e.Tag != 0xA005 && e.Tag != 0x9286
                                    && e.Data.Length > 0 && e.Data.Length <= 1024).ToList();
        bool hasExif = keep1.Count > 0;
        if (hasExif) keep0.Add(new Entry(0x8769, 4, 1, new byte[4]));
        if (keep0.Count == 0) return null;

        keep0 = keep0.OrderBy(e => e.Tag).ToList();
        keep1 = keep1.OrderBy(e => e.Tag).ToList();

        int exifOff = 8 + 2 + 12 * keep0.Count + 4;
        int dataStart = hasExif ? exifOff + 2 + 12 * keep1.Count + 4 : exifOff;

        var blob = new MemoryStream();
        var ms = new MemoryStream();
        ms.Write("II*\0"u8);
        W32(ms, 8);
        WriteIfd(ms, keep0, hasExif ? exifOff : 0, dataStart, blob);
        if (hasExif) WriteIfd(ms, keep1, 0, dataStart, blob);
        blob.Position = 0;
        blob.CopyTo(ms);

        var tiff = ms.ToArray();
        int len = 2 + 6 + tiff.Length;
        if (len > 65535) return null;

        var seg = new byte[2 + len];
        seg[0] = 0xFF; seg[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(2), (ushort)len);
        "Exif\0\0"u8.CopyTo(seg.AsSpan(4));
        tiff.CopyTo(seg, 10);
        return seg;
    }

    static void WriteIfd(MemoryStream ms, List<Entry> ents, int exifPointer, int dataStart, MemoryStream blob)
    {
        W16(ms, (ushort)ents.Count);
        foreach (var e in ents)
        {
            W16(ms, e.Tag); W16(ms, e.Type); W32(ms, e.Count);
            if (e.Tag == 0x8769 && exifPointer > 0)
            {
                W32(ms, (uint)exifPointer);
            }
            else if (e.Data.Length <= 4)
            {
                ms.Write(e.Data);
                for (int i = e.Data.Length; i < 4; i++) ms.WriteByte(0);
            }
            else
            {
                W32(ms, (uint)(dataStart + blob.Length));
                blob.Write(e.Data);
                if ((blob.Length & 1) == 1) blob.WriteByte(0);
            }
        }
        W32(ms, 0);
    }

    public static Info Extract(Stream src, string dst, DateTime fallbackTime)
    {
        var head = ReadAt(src, 0, 16);
        if (!(head[0] == 'I' && head[1] == 'I' && head[2] == 0x2A && head[3] == 0 && head[8] == 'C' && head[9] == 'R'))
            throw new InvalidDataException("This is not a Canon CR2 file.");

        long off = U32(head, 4);
        long rawIfd = U32(head, 12);

        List<Entry>? ifd0 = null;
        long bestOff = 0, bestLen = 0;
        int bestW = 0, bestH = 0;

        for (int guard = 0; off != 0 && guard < 8; guard++)
        {
            var (ents, next) = ReadIfd(src, off);
            ifd0 ??= ents;

            int w = (int)Num(Find(ents, 0x100)), h = (int)Num(Find(ents, 0x101));
            long so = Num(Find(ents, 0x111)), sc = Num(Find(ents, 0x117));

            // A JPEG preview: compression 6, has a size, and is not the raw sensor data block
            if (off != rawIfd && Num(Find(ents, 0x103)) == 6 && w > 0 && h > 0 && so > 0 && sc > 2
                && (long)w * h > (long)bestW * bestH)
            {
                var magic = ReadAt(src, so, 2);
                if (magic[0] == 0xFF && magic[1] == 0xD8)
                {
                    if (so + sc > src.Length)
                        throw new InvalidDataException("This file is cut off or damaged. It may not have copied fully from the card.");
                    bestOff = so; bestLen = sc; bestW = w; bestH = h;
                }
            }
            off = next;
        }

        if (bestLen == 0 || ifd0 is null)
            throw new InvalidDataException("No full-size JPEG was found inside this CR2.");

        // Optional metadata: never fatal if it fails
        byte[]? exifSeg = null;
        int fullWidth = 0;
        DateTime when = fallbackTime;
        try
        {
            var ptr = Find(ifd0, 0x8769);
            var exif = ptr is null ? new List<Entry>() : ReadIfd(src, Num(ptr)).Entries;
            fullWidth = (int)Num(Find(exif, 0xA002));
            exifSeg = BuildExifSegment(ifd0, exif);

            var dt = Find(ifd0, 0x132);
            if (dt != null && DateTime.TryParseExact(Encoding.ASCII.GetString(dt.Data).TrimEnd('\0', ' '),
                    "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                when = parsed;
        }
        catch { }

        // Write to a .part file first so a stopped run never leaves a half-written .jpg
        var tmp = dst + ".part";
        long written = 0;
        try
        {
            using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 262144, FileOptions.SequentialScan))
            {
                o.WriteByte(0xFF); o.WriteByte(0xD8);
                written += 2;
                if (exifSeg != null) { o.Write(exifSeg); written += exifSeg.Length; }

                var buf = new byte[262144];
                long left = bestLen - 2;
                src.Seek(bestOff + 2, SeekOrigin.Begin);
                while (left > 0)
                {
                    int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) throw new InvalidDataException("The file ended unexpectedly while copying the image.");
                    o.Write(buf, 0, n);
                    left -= n; written += n;
                }
            }
            File.Move(tmp, dst, true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        try { File.SetCreationTime(dst, when); File.SetLastWriteTime(dst, when); } catch { }

        string? warning = null;
        if (fullWidth > 0 && bestW < fullWidth * 0.9)
            warning = $"saved a smaller {bestW} x {bestH} preview (camera sensor width is {fullWidth}).";

        return new Info(bestW, bestH, written, warning);
    }
}

// ---------------------------------------------------------------------
//  Front end. Palette: #ff9505 primary, #ffb627 secondary, #ffcb69 ternary
// ---------------------------------------------------------------------
static class Html
{
    public const string Page = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>CR2 to JPEG Converter</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:wght@500;700;800&family=DM+Sans:wght@400;500;700&display=swap" rel="stylesheet">
<style>
  :root{
    --primary:#ff9505; --secondary:#ffb627; --ternary:#ffcb69;
    --ink:#2b1700; --ink-soft:#6b4a1f; --panel:#fff8ea; --line:rgba(43,23,0,.14);
    --ok:#1d7a3a; --bad:#b3261e;
  }
  *{box-sizing:border-box}
  html,body{margin:0}
  body{
    font-family:'DM Sans',system-ui,Segoe UI,Arial,sans-serif; color:var(--ink);
    background:
      radial-gradient(900px 500px at 100% 0%, var(--secondary), transparent 70%),
      radial-gradient(700px 500px at 0% 100%, var(--primary), transparent 65%),
      var(--ternary);
    background-attachment:fixed;
    min-height:100vh; line-height:1.5;
  }
  .wrap{max-width:1040px;margin:0 auto;padding:40px 20px 64px}
  header{display:flex;align-items:center;gap:28px;flex-wrap:wrap;margin-bottom:32px}
  .lens{
    width:112px;height:112px;border-radius:50%;flex:none;position:relative;
    background:conic-gradient(from 0deg,var(--primary),var(--secondary),var(--ternary),var(--primary));
    box-shadow:0 0 0 6px var(--ink), 0 10px 24px rgba(43,23,0,.25);
    display:grid;place-items:center;
  }
  .lens::before{content:"";width:58px;height:58px;border-radius:50%;background:var(--ink);box-shadow:inset 0 0 0 6px #4a2c08}
  .lens::after{content:"";position:absolute;width:14px;height:14px;border-radius:50%;background:#fff8ea;top:38px;left:42px;opacity:.9}
  .lens.spin{animation:turn 2.4s linear infinite}
  @keyframes turn{to{transform:rotate(360deg)}}
  h1{font-family:'Bricolage Grotesque',sans-serif;font-weight:800;font-size:clamp(2rem,5vw,3.4rem);line-height:1.02;margin:0 0 8px;letter-spacing:-.02em}
  header p{margin:0;max-width:52ch;color:var(--ink-soft);font-size:1.05rem}

  .tabs{display:flex;gap:8px;margin-bottom:-1px;position:relative;z-index:1}
  .tab{
    font:700 1rem 'Bricolage Grotesque',sans-serif;border:2px solid var(--ink);border-bottom:none;
    background:var(--ternary);color:var(--ink);padding:12px 22px;border-radius:14px 14px 0 0;cursor:pointer
  }
  .tab[aria-selected="true"]{background:var(--panel)}
  .tab:focus-visible,button:focus-visible,input:focus-visible,.drop:focus-visible{outline:3px solid var(--ink);outline-offset:2px}
  .card{background:var(--panel);border:2px solid var(--ink);border-radius:0 18px 18px 18px;padding:28px;box-shadow:8px 8px 0 var(--ink)}
  .hidden{display:none}

  label{display:block;font-weight:700;margin-bottom:6px}
  .hint{font-weight:400;color:var(--ink-soft);font-size:.92rem;margin:4px 0 0}
  input[type=text]{
    width:100%;padding:12px 14px;font:inherit;color:var(--ink);background:#fff;
    border:2px solid var(--ink);border-radius:10px
  }
  .grid{display:grid;grid-template-columns:1fr 1fr;gap:20px}
  @media(max-width:720px){.grid{grid-template-columns:1fr}}
  .row{margin-bottom:20px}
  .checks{display:flex;gap:24px;flex-wrap:wrap;margin:6px 0 24px}
  .checks label{display:flex;align-items:center;gap:8px;font-weight:500;margin:0}
  input[type=checkbox]{width:20px;height:20px;accent-color:var(--primary)}
  .note{background:var(--ternary);border:2px solid var(--ink);border-radius:12px;padding:14px 18px;margin-bottom:22px}
  .note b{font-family:'Bricolage Grotesque',sans-serif}

  button.go{
    font:800 1.1rem 'Bricolage Grotesque',sans-serif;color:var(--ink);background:var(--primary);
    border:2px solid var(--ink);border-radius:12px;padding:14px 28px;cursor:pointer;box-shadow:4px 4px 0 var(--ink);
    transition:transform .12s,box-shadow .12s
  }
  button.go:hover{transform:translate(-2px,-2px);box-shadow:6px 6px 0 var(--ink)}
  button.go:active{transform:translate(2px,2px);box-shadow:1px 1px 0 var(--ink)}
  button.go:disabled{opacity:.5;cursor:not-allowed;transform:none;box-shadow:4px 4px 0 var(--ink)}
  button.alt{background:var(--secondary)}
  .actions{display:flex;gap:14px;flex-wrap:wrap}

  .progress{display:flex;gap:28px;align-items:center;flex-wrap:wrap;margin-top:28px;padding-top:24px;border-top:2px dashed var(--line)}
  .ring{width:170px;height:170px;border-radius:50%;display:grid;place-items:center;flex:none;
    background:conic-gradient(var(--primary) calc(var(--p,0)*1%), rgba(43,23,0,.12) 0)}
  .ring::before{content:"";grid-area:1/1;width:134px;height:134px;border-radius:50%;background:var(--panel)}
  .ring span{grid-area:1/1;z-index:1;font:800 2rem 'Bricolage Grotesque',sans-serif}
  .stats{flex:1;min-width:240px}
  .stats dl{display:grid;grid-template-columns:auto 1fr;gap:6px 18px;margin:0 0 10px}
  .stats dt{color:var(--ink-soft)} .stats dd{margin:0;font-weight:700;word-break:break-all}
  .msg{margin-top:16px;padding:12px 16px;border-radius:10px;border:2px solid var(--ink);background:var(--ternary);font-weight:500}
  .msg.err{background:#ffd9d4;border-color:var(--bad);color:#5c120d}
  .errlist{margin:8px 0 0;padding-left:18px;font-size:.9rem;color:#5c120d}

  .drop{
    border:3px dashed var(--ink);border-radius:16px;background:linear-gradient(135deg,var(--ternary),#ffe3a8);
    padding:44px 20px;text-align:center;cursor:pointer;transition:background .15s
  }
  .drop.over{background:var(--secondary)}
  .drop strong{font:800 1.4rem 'Bricolage Grotesque',sans-serif;display:block;margin-bottom:4px}
  .files{list-style:none;margin:24px 0 0;padding:0;display:grid;gap:12px}
  .file{display:grid;grid-template-columns:1fr auto;gap:14px;align-items:center;background:#fff;border:2px solid var(--ink);border-radius:12px;padding:10px 14px}
  .fname{font-weight:700;word-break:break-all}
  .fstate{font-size:.9rem;color:var(--ink-soft)}
  .bar{height:8px;border-radius:99px;background:rgba(43,23,0,.12);margin-top:6px;overflow:hidden}
  .bar i{display:block;height:100%;width:0;background:var(--primary);transition:width .2s}
  .file a{font-weight:700;color:var(--ink);background:var(--secondary);border:2px solid var(--ink);border-radius:8px;padding:6px 12px;text-decoration:none}
  .file.bad{border-color:var(--bad)} .file.bad .fstate{color:var(--bad)}
  footer{margin-top:28px;color:var(--ink-soft);font-size:.92rem}
  @media (prefers-reduced-motion:reduce){.lens.spin{animation:none} button.go{transition:none}}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <div class="lens" id="lens" aria-hidden="true"></div>
    <div>
      <h1>CR2 to JPEG converter</h1>
      <p>Turn Canon RAW photos into full-size JPEGs. Convert a whole folder at once, or drop in a few files.</p>
    </div>
  </header>

  <div class="tabs" role="tablist">
    <button class="tab" role="tab" id="t1" aria-selected="true" aria-controls="p1">Convert a folder</button>
    <button class="tab" role="tab" id="t2" aria-selected="false" aria-controls="p2">Upload files</button>
  </div>

  <!-- FOLDER MODE -->
  <section class="card" id="p1" role="tabpanel">
    <div class="note"><b>Light on your PC.</b> Each photo is read from the JPEG your camera saved inside the CR2: full size, no extra compression, usually 3 to 6 MB. One file at a time, so your computer stays responsive.</div>
    <div class="grid">
      <div class="row">
        <label for="inDir">Folder with CR2 files</label>
        <input type="text" id="inDir" placeholder="D:\Photos\RAW" autocomplete="off">
        <p class="hint">Path on the computer running this app.</p>
      </div>
      <div class="row">
        <label for="outDir">Save JPEGs to (optional)</label>
        <input type="text" id="outDir" placeholder="D:\Photos\JPEG" autocomplete="off">
        <p class="hint">Leave empty to create a "jpeg" folder inside the input folder.</p>
      </div>
    </div>
    <div class="checks">
      <label><input type="checkbox" id="rec" checked> Include subfolders</label>
      <label><input type="checkbox" id="skip" checked> Skip files already converted</label>
    </div>
    <div class="actions">
      <button class="go" id="start">Start converting</button>
      <button class="go alt" id="cancel" disabled>Stop</button>
    </div>
    <div id="fmsg"></div>
    <div class="progress hidden" id="prog">
      <div class="ring" id="ring"><span id="pct">0%</span></div>
      <div class="stats">
        <dl>
          <dt>Converted</dt><dd id="sDone">0 of 0</dd>
          <dt>Now working on</dt><dd id="sCur">-</dd>
          <dt>JPEG size so far</dt><dd id="sSize">0 MB</dd>
          <dt>Skipped</dt><dd id="sSkip">0</dd>
          <dt>Failed</dt><dd id="sFail">0</dd>
          <dt>Saved in</dt><dd id="sOut">-</dd>
        </dl>
        <ul class="errlist" id="errs"></ul>
      </div>
    </div>
  </section>

  <!-- UPLOAD MODE -->
  <section class="card hidden" id="p2" role="tabpanel">
    <div class="drop" id="drop" tabindex="0" role="button" aria-label="Choose CR2 files">
      <strong>Drop CR2 files here</strong>
      or click to choose files
      <input type="file" id="pick" accept=".cr2,.CR2" multiple hidden>
    </div>
    <ul class="files" id="files"></ul>
    <p class="hint">Files are sent one after another. JPEGs are also kept in the "converted" folder next to the app. For big batches, use the folder tab instead.</p>
  </section>

  <footer>Everything runs on your own computer. Nothing is sent to the internet.</footer>
</div>

<script>
const $ = s => document.querySelector(s);
const fmt = b => b > 1073741824 ? (b/1073741824).toFixed(2)+' GB' : b > 1048576 ? (b/1048576).toFixed(1)+' MB' : Math.max(1,Math.round(b/1024))+' KB';

/* ---------- tabs ---------- */
function showTab(n){
  $('#p1').classList.toggle('hidden', n!==1);
  $('#p2').classList.toggle('hidden', n!==2);
  $('#t1').setAttribute('aria-selected', n===1);
  $('#t2').setAttribute('aria-selected', n===2);
}
$('#t1').onclick = () => showTab(1);
$('#t2').onclick = () => showTab(2);

/* ---------- folder mode ---------- */
let jobId = null, timer = null;

function msg(text, bad){
  $('#fmsg').innerHTML = text ? `<div class="msg ${bad?'err':''}"></div>` : '';
  if(text) $('#fmsg').firstChild.textContent = text;
}

$('#start').onclick = async () => {
  msg('');
  const body = {
    input: $('#inDir').value.trim(),
    output: $('#outDir').value.trim(),
    recursive: $('#rec').checked,
    skipExisting: $('#skip').checked
  };
  if(!body.input){ msg('Enter the folder that contains your CR2 files.', true); return; }
  const r = await fetch('/api/folder/start', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(body)});
  const j = await r.json();
  if(!r.ok){ msg(j.error || 'Could not start.', true); return; }
  jobId = j.id;
  $('#prog').classList.remove('hidden');
  $('#start').disabled = true; $('#cancel').disabled = false;
  $('#lens').classList.add('spin');
  timer = setInterval(poll, 1000); poll();
};

$('#cancel').onclick = () => { if(jobId) fetch('/api/folder/cancel/'+jobId, {method:'POST'}); };

async function poll(){
  const r = await fetch('/api/folder/status/'+jobId);
  if(!r.ok) return;
  const s = await r.json();
  const p = s.total ? Math.round(s.done / s.total * 100) : 0;
  $('#ring').style.setProperty('--p', p);
  $('#pct').textContent = p + '%';
  $('#sDone').textContent = `${s.done} of ${s.total}`;
  $('#sCur').textContent = s.finished ? '-' : (s.current || '-');
  $('#sSize').textContent = fmt(s.outBytes);
  $('#sSkip').textContent = s.skipped;
  $('#sFail').textContent = s.failed;
  $('#sOut').textContent = s.output;
  $('#errs').innerHTML = '';
  s.notes.forEach(e => { const li = document.createElement('li'); li.textContent = e; $('#errs').appendChild(li); });
  if(s.finished){
    clearInterval(timer);
    $('#start').disabled = false; $('#cancel').disabled = true;
    $('#lens').classList.remove('spin');
    msg(s.cancelled ? 'Stopped. Start again with "Skip files already converted" ticked to continue.'
                    : (s.failed ? `Finished. ${s.failed} file(s) could not be converted. See the list above.` : 'All done. Your JPEGs are ready.'),
        s.failed > 0);
  }
}

/* ---------- upload mode ---------- */
const drop = $('#drop'), pick = $('#pick');
drop.onclick = () => pick.click();
drop.onkeydown = e => { if(e.key==='Enter' || e.key===' '){ e.preventDefault(); pick.click(); } };
['dragenter','dragover'].forEach(ev => drop.addEventListener(ev, e => { e.preventDefault(); drop.classList.add('over'); }));
['dragleave','drop'].forEach(ev => drop.addEventListener(ev, e => { e.preventDefault(); drop.classList.remove('over'); }));
drop.addEventListener('drop', e => addFiles(e.dataTransfer.files));
pick.onchange = () => { addFiles(pick.files); pick.value = ''; };

const queue = []; let busy = false;

function addFiles(list){
  [...list].forEach(f => {
    if(!/\.cr2$/i.test(f.name)) return;
    const li = document.createElement('li');
    li.className = 'file';
    li.innerHTML = `<div><div class="fname"></div><div class="fstate">Waiting</div><div class="bar"><i></i></div></div><span></span>`;
    li.querySelector('.fname').textContent = f.name + '  (' + fmt(f.size) + ')';
    $('#files').appendChild(li);
    queue.push({file:f, li});
  });
  runQueue();
}

async function runQueue(){
  if(busy) return; busy = true;
  $('#lens').classList.add('spin');
  while(queue.length){ await uploadOne(queue.shift()); }
  $('#lens').classList.remove('spin');
  busy = false;
}

function uploadOne({file, li}){
  return new Promise(resolve => {
    const state = li.querySelector('.fstate'), bar = li.querySelector('.bar i');
    const fd = new FormData();
    fd.append('file', file);
    const x = new XMLHttpRequest();
    x.open('POST', '/api/upload');
    x.upload.onprogress = e => {
      if(!e.lengthComputable) return;
      const p = Math.round(e.loaded / e.total * 100);
      bar.style.width = p + '%';
      state.textContent = p < 100 ? `Uploading ${p}%` : 'Converting...';
    };
    x.onload = () => {
      let j = {}; try { j = JSON.parse(x.responseText); } catch {}
      if(x.status === 200){
        bar.style.width = '100%';
        state.textContent = `Done: ${j.width} x ${j.height}, ${fmt(j.size)}` + (j.warning ? ' (' + j.warning + ')' : '');
        li.lastElementChild.outerHTML = `<a href="${j.url}?download=true">Download</a>`;
      } else {
        li.classList.add('bad');
        state.textContent = j.error || 'Conversion failed.';
      }
      resolve();
    };
    x.onerror = () => { li.classList.add('bad'); state.textContent = 'Upload failed. Is the app still running?'; resolve(); };
    x.send(fd);
  });
}
</script>
</body>
</html>
""";
}
