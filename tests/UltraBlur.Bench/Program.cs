using UltraBlur.Plugin.Algorithm;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: UltraBlur.Bench <expected.txt> <imageDir>");
    return 2;
}

int ok = 0, bad = 0;
var sw = System.Diagnostics.Stopwatch.StartNew();
foreach (var line in File.ReadLines(args[0]))
{
    var parts = line.Split(' ', 2);
    if (parts.Length < 2 || !(parts[1].StartsWith("topLeft=", StringComparison.Ordinal) || parts[1].StartsWith("extraction failed", StringComparison.Ordinal)))
    {
        continue;
    }

    var img = ImageLoader.Load(Path.Combine(args[1], parts[0]));
    var c = img == null ? null : UltraBlurCore.Extract(img);
    var got = c == null
        ? "extraction failed"
        : $"topLeft={c.Value.Tl:x6} topRight={c.Value.Tr:x6} bottomRight={c.Value.Br:x6} bottomLeft={c.Value.Bl:x6}";
    if (got == parts[1].Trim())
    {
        ok++;
    }
    else
    {
        bad++;
        Console.WriteLine($"MISMATCH {parts[0]}\n  want {parts[1].Trim()}\n  got  {got}");
    }
}

Console.WriteLine($"{ok}/{ok + bad} identical ({sw.ElapsedMilliseconds} ms)");
return bad == 0 ? 0 : 1;
