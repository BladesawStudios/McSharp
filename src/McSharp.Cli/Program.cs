using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using McSharp;

namespace McSharp.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        switch (args[0])
        {
            case "-d" or "--decompress" when args.Length >= 3:
                return Decompress(args[1], args[2]) ? 0 : 1;
            case "-c" or "--compress" when args.Length >= 2:
                return Compress(args[1], args.Length >= 3 ? args[2] : args[1] + ".mc") ? 0 : 1;
            case "--verify" when args.Length >= 3:
                MeshCodec.FlushDenormalHalves = args.Contains("--flush-denormal-halves");
                return Verify(args[1], args[2]);
            default:
                if (args.Length >= 2 && !args[0].StartsWith('-'))
                    return Decompress(args[0], args[1]) ? 0 : 1;
                Usage();
                return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("McSharp CLI");
        Console.WriteLine();
        Console.WriteLine("  Decompress a file or directory:");
        Console.WriteLine("    mcsharp <input_path> <output_dir>");
        Console.WriteLine();
        Console.WriteLine("  Compress a BFRES into a .bfres.mc package:");
        Console.WriteLine("    mcsharp -c <input_path> [output_path]");
        Console.WriteLine();
        Console.WriteLine("  Compare decoded output against a reference directory:");
        Console.WriteLine("    mcsharp --verify <input_path> <reference_dir> [--flush-denormal-halves]");
    }

    private static IEnumerable<string> EnumerateInputs(string path)
    {
        if (File.Exists(path))
        {
            yield return path;
            yield break;
        }

        if (!Directory.Exists(path))
            yield break;

        foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(f);
            if (ext is ".mc" or ".chunk")
                yield return f;
        }
    }

    private static byte[]? DecodeOne(string file, byte[] work)
    {
        byte[] data = File.ReadAllBytes(file);

        if (Path.GetExtension(file) == ".chunk")
        {
            if (!MeshCodec.TryReadChunkHeader(data, out ResChunkHeader ch))
                return null;
            byte[] outBuf = new byte[ch.DecompressedSize];
            return MeshCodec.DecompressChunk(outBuf, data, work) ? outBuf : null;
        }

        if (!MeshCodec.TryReadPackageHeader(data, out ResMeshCodecPackageHeader ph))
            return null;
        byte[] dst = new byte[ph.GetDecompressedSize()];
        return MeshCodec.DecompressMc(dst, data, work) ? dst : null;
    }

    private static bool Decompress(string input, string outputDir)
    {
        byte[] work = new byte[MeshCodec.DefaultWorkBufferSize];
        int ok = 0, failed = 0;

        foreach (string file in EnumerateInputs(input))
        {
            byte[]? result;
            try
            {
                result = DecodeOne(file, work);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EXCEPTION] {Path.GetFileName(file)}: {ex.Message}");
                failed++;
                continue;
            }

            if (result == null)
            {
                Console.WriteLine($"[FAIL] {Path.GetFileName(file)}");
                failed++;
                continue;
            }

            string rel = File.Exists(input)
                ? Path.GetFileNameWithoutExtension(file)
                : Path.Combine(Path.GetRelativePath(input, Path.GetDirectoryName(file)!), Path.GetFileNameWithoutExtension(file));
            string outPath = Path.Combine(outputDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, result);
            Console.WriteLine(Path.GetFileName(file));
            ok++;
        }

        Console.WriteLine($"\n{ok} decompressed, {failed} failed.");
        return failed == 0 && ok > 0;
    }

    private static bool Compress(string input, string output)
    {
        byte[] data = File.ReadAllBytes(input);
        byte[] package = McEncoder.CompressMc(data, new EncoderOptions { ZstdLevel = 19 });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllBytes(output, package);
        Console.WriteLine($"[OK] {Path.GetFileName(input)} ({data.Length} B -> {package.Length} B) -> {output}");
        return true;
    }

    private static int Verify(string input, string referenceDir)
    {
        byte[] work = new byte[MeshCodec.DefaultWorkBufferSize];
        int match = 0, mismatch = 0, missing = 0, failed = 0;
        Stopwatch sw = Stopwatch.StartNew();

        foreach (string file in EnumerateInputs(input))
        {
            string stem = Path.GetFileNameWithoutExtension(file);

            string refPath = File.Exists(input)
                ? Path.Combine(referenceDir, stem)
                : Path.Combine(referenceDir, Path.GetRelativePath(input, Path.GetDirectoryName(file)!), stem);
            if (!File.Exists(refPath))
            {
                missing++;
                continue;
            }

            byte[]? result;
            try
            {
                result = DecodeOne(file, work);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EXCEPTION] {stem}: {ex.GetType().Name}: {ex.Message}");
                failed++;
                continue;
            }

            if (result == null)
            {
                Console.WriteLine($"[FAIL] {stem}: decode returned failure");
                failed++;
                continue;
            }

            byte[] expected = File.ReadAllBytes(refPath);
            if (expected.Length != result.Length)
            {
                Console.WriteLine($"[SIZE] {stem}: got {result.Length}, expected {expected.Length}");
                mismatch++;
                continue;
            }

            int diff = -1;
            for (int i = 0; i < expected.Length; ++i)
            {
                if (expected[i] != result[i]) { diff = i; break; }
            }

            if (diff >= 0)
            {
                Console.WriteLine($"[DIFF] {stem}: first difference at 0x{diff:x} (got 0x{result[diff]:x2}, expected 0x{expected[diff]:x2})");
                mismatch++;
            }
            else
            {
                match++;
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine("=========================================");
        Console.WriteLine($" Byte-identical : {match}");
        Console.WriteLine($" Mismatched     : {mismatch}");
        Console.WriteLine($" Decode failed  : {failed}");
        Console.WriteLine($" No reference   : {missing}");
        Console.WriteLine($" Elapsed        : {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine("=========================================");

        return (mismatch == 0 && failed == 0 && match > 0) ? 0 : 1;
    }
}
