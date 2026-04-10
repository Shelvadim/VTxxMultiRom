using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using VT03Builder.Models;
using VT03Builder.Services;
using VT03Builder.Services.SourceMappers;
using VT03Builder.Services.Targets;

namespace VT03Builder
{
    internal static class Program
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Entry point — GUI when run with no arguments, CLI otherwise.
        //
        //  CLI usage:
        //    VT03Builder.exe game1.nes game2.nes [options]
        //
        //  Options:
        //    --size  <MB>          NOR chip size in MB   (default: 8)
        //    --out-bin <path>      Output .bin file path (default: multicart.bin)
        //    --no-nes              Skip .nes / .unf test file generation
        //    --submapper <n>       NES 2.0 submapper 0–15 (default: 0)
        //    --help                Show this help and exit
        //
        //  Submapper values (mapper 256 / OneBus):
        //    0  Normal               5  Waixing VT02
        //    1  Waixing VT03        11  Vibes
        //    2  Power Joy Supermax  12  Cheertone
        //    3  Zechess/Hummer Team 13  Cube Tech
        //    4  Sports Game 69-in-1 14  Karaoto
        //                           15  Jungletac
        // ─────────────────────────────────────────────────────────────────────

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                // ── GUI mode ─────────────────────────────────────────────────
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Forms.MainForm());
                return 0;
            }

            // ── CLI mode ──────────────────────────────────────────────────────
            return RunCli(args);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static int RunCli(string[] args)
        {
            // ── Parse arguments ───────────────────────────────────────────────
            var gameFiles   = new List<string>();
            int  chipSizeMb = 8;
            string outBin   = "multicart.bin";
            bool genNes     = true;
            int  submapper  = 0;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        PrintHelp();
                        return 0;

                    case "--size":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out chipSizeMb))
                        { Error("--size requires an integer (e.g. 8)"); return 1; }
                        if (chipSizeMb is not (2 or 4 or 8 or 16 or 32))
                        { Error("--size must be 2, 4, 8, 16, or 32"); return 1; }
                        break;

                    case "--out-bin":
                        if (i + 1 >= args.Length) { Error("--out-bin requires a path"); return 1; }
                        outBin = args[++i];
                        break;

                    case "--no-nes":
                        genNes = false;
                        break;

                    case "--submapper":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out submapper))
                        { Error("--submapper requires an integer 0–15"); return 1; }
                        if (submapper < 0 || submapper > 15)
                        { Error("--submapper must be 0–15"); return 1; }
                        break;

                    default:
                        if (args[i].StartsWith("-"))
                        { Error($"Unknown option: {args[i]}  (run --help for usage)"); return 1; }
                        gameFiles.Add(args[i]);
                        break;
                }
            }

            if (gameFiles.Count == 0)
            {
                Error("No .nes files specified.  Run --help for usage.");
                return 1;
            }

            // ── Load ROMs ─────────────────────────────────────────────────────
            var  games     = new List<NesRom>();
            bool anyFailed = false;

            foreach (string path in gameFiles)
            {
                if (!File.Exists(path))
                {
                    Warn($"File not found: {path}");
                    anyFailed = true;
                    continue;
                }

                var rom = NesRom.Load(path);
                if (!rom.IsValid)
                {
                    Warn($"SKIP  {Path.GetFileName(path)}: {rom.ParseError}");
                    anyFailed = true;
                    continue;
                }
                if (!SourceMapperRegistry.IsSupported(rom.Mapper))
                {
                    Warn($"SKIP  {rom.FileName}: mapper {rom.Mapper} not supported " +
                         $"(use 0/NROM, 4/MMC3)");
                    anyFailed = true;
                    continue;
                }
                if (rom.HasChrRam && rom.Mapper == 4)
                {
                    Warn($"SKIP  {rom.FileName}: CHR-RAM MMC3 games grey-screen on VT03 hardware");
                    anyFailed = true;
                    continue;
                }

                var vtxxTarget = TargetRegistry.GetRequired("vtxx");
                string? compatWarn = SourceMapperRegistry.Get(rom.Mapper)
                                         ?.CompatibilityWarning(rom, vtxxTarget);
                if (compatWarn != null)
                    Warn($"WARN  {rom.FileName}: {compatWarn}");

                Ok($"  OK  {rom.FileName}  [{rom.MapperDescription}]  " +
                   $"PRG:{rom.PrgSize / 1024}KB  " +
                   $"CHR:{(rom.ChrSize > 0 ? rom.ChrSize / 1024 + "KB" : "RAM")}");
                games.Add(rom);
            }

            if (games.Count == 0)
            {
                Error("No compatible games loaded. Nothing to build.");
                return 1;
            }

            // ── Build ─────────────────────────────────────────────────────────
            if (!outBin.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                outBin += ".bin";

            var cfg = new BuildConfig
            {
                Games       = games,
                ChipSizeMb  = chipSizeMb,
                OutputPath  = outBin,
                GenerateNes = genNes,
                Submapper   = submapper,
            };

            Console.WriteLine();
            Console.WriteLine($"Building:  {games.Count} game(s)  |  " +
                              $"{chipSizeMb} MB chip  |  " +
                              $"mapper 256  |  submapper {submapper}");
            Console.WriteLine();

            BuildResult result;
            try
            {
                result = RomBuilder.Build(cfg, new Progress<string>(msg =>
                {
                    if      (msg.StartsWith("SKIP") || msg.StartsWith("ERR")) Warn("  " + msg);
                    else if (msg.StartsWith("NROM") || msg.StartsWith("MMC3")) Ok("  " + msg);
                    else    Console.WriteLine("  " + msg);
                }));
            }
            catch (Exception ex)
            {
                Error($"Build failed: {ex.Message}");
                return 1;
            }

            // ── Write outputs ─────────────────────────────────────────────────
            File.WriteAllBytes(outBin, result.NorBinary);
            OkBold($"\nBIN  {outBin}  ({result.NorBinary.Length / 1024} KB)");

            if (genNes)
            {
                string nesPath = Path.ChangeExtension(outBin, ".nes");
                string unfPath = Path.ChangeExtension(outBin, ".unf");
                File.WriteAllBytes(nesPath, result.NesFile);
                File.WriteAllBytes(unfPath, result.UnifFile);
                OkBold($"NES  {nesPath}  (mapper 256  submapper {submapper})");
                OkBold($"UNF  {unfPath}  (UNL-OneBus UNIF)");
            }

            Console.WriteLine();
            Console.WriteLine($"  Games  : {result.GameCount}");
            Console.WriteLine($"  NROM   : {result.NromUsed  / 1024} KB used");
            Console.WriteLine($"  MMC3   : {result.Mmc3Used  / 1024} KB used");

            if (anyFailed)
            {
                Warn("\n  Some files were skipped — see warnings above.");
                return 2;  // partial success
            }

            return 0;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void PrintHelp()
        {
            Console.WriteLine("""
VT03 OneBus NOR Flash Multicart Builder
========================================
GUI mode:   VT03Builder.exe
CLI mode:   VT03Builder.exe game1.nes game2.nes [...] [options]

Options:
  --size <MB>        NOR chip size: 2, 4, 8 (default), 16, 32
  --out-bin <path>   Output .bin path           (default: multicart.bin)
  --no-nes           Skip .nes and .unf test files
  --submapper <n>    NES 2.0 submapper 0-15     (default: 0)
  --help             Show this help

Submappers (mapper 256 / OneBus):
   0  Normal (SUP/RetroFC)      5  Waixing VT02
   1  Waixing VT03             11  Vibes
   2  Power Joy Supermax       12  Cheertone
   3  Zechess / Hummer Team    13  Cube Tech
   4  Sports Game 69-in-1      14  Karaoto
                                15  Jungletac

Output files:
  .bin  ->  flash to NOR chip via T48 / Xgpro
  .nes  ->  NintendulatorNRS or FCEUX (NROM/CNROM only in FCEUX)
  .unf  ->  NintendulatorNRS (UNL-OneBus, full MMC3 support)

Supported mappers:  0 NROM  |  3 CNROM  |  4 MMC3
MMC3 notes:  CHR-RAM games are rejected (grey screen on VT03 hardware).
             PRG > 256 KB games are allowed with a warning (IRQ timing may fail).
""");
        }

        // ── Console colour helpers ────────────────────────────────────────────
        private static void Ok(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(msg);
            Console.ResetColor();
        }

        private static void OkBold(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(msg);
            Console.ResetColor();
        }

        private static void Warn(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(msg);
            Console.ResetColor();
        }

        private static void Error(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"ERROR: {msg}");
            Console.ResetColor();
        }
    }
}
