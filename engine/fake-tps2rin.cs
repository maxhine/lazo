// A stand-in for Topcon's TPS2RIN for the tests: same command line (-q -o <dir> -i <file>), writes small RINEX 2 files named
// like the real one (<base>.26O, .26N, .26G). The epochs: 21:02:25 to 21:02:34 every second, with 21:02:29 to 21:02:31 missing.
using System;
using System.IO;
using System.Text;

internal static class Fake
{
    private static string H(string text, string label) { return text.PadRight(60) + label; }

    private static int Main(string[] args)
    {
        string input = null, output = ".";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-i") input = args[++i];
            else if (args[i] == "-o") output = args[++i];
        }
        if (input == null || !File.Exists(input)) { Console.WriteLine("Can not open input file"); return 1; }
        if (new FileInfo(input).Length == 0) { Console.WriteLine("Empty TPS file"); return 2; }
        string name = Path.GetFileNameWithoutExtension(input);
        StringBuilder o = new StringBuilder();
        o.AppendLine(H("     2.10           OBSERVATION DATA    M (MIXED)", "RINEX VERSION / TYPE"));
        o.AppendLine(H("TPS2RIN 1.40        RUN BY              22-OCT-26 14:08", "PGM / RUN BY / DATE"));
        o.AppendLine(H(name + "_M490", "MARKER NAME"));
        o.AppendLine(H("Q1FI1PHM490         TPS BX10            4.8 OEM Jun,24,2021", "REC # / TYPE / VERS"));
        o.AppendLine(H(name + "            -Unknown-", "ANT # / TYPE"));
        o.AppendLine(H("  2026    10    21    21     2   25.0000000     GPS", "TIME OF FIRST OBS"));
        o.AppendLine(H("     1.000", "INTERVAL"));
        o.AppendLine(H("    18", "LEAP SECONDS"));
        o.AppendLine(H("     2", "# OF SATELLITES"));
        o.AppendLine(H("     2    C1    L1", "# / TYPES OF OBSERV"));
        o.AppendLine(H("   G 2     7     7", "PRN / # OF OBS"));
        o.AppendLine(H("   R 3     7     7", "PRN / # OF OBS"));
        o.AppendLine(H("", "END OF HEADER"));
        foreach (int s in new[] { 25, 26, 27, 28, 32, 33, 34 })
        {
            o.AppendLine(" 26 10 21 21  2 " + s.ToString("00") + ".0000000  0  2G 2R 3");
            o.AppendLine("  22345678.123 7  117420000.123 7");
            o.AppendLine("  21345678.456 7  112420000.456 7");
        }
        File.WriteAllText(Path.Combine(output, name + ".26O"), o.ToString());
        File.WriteAllText(Path.Combine(output, name + ".26N"), "nav gps\n");
        File.WriteAllText(Path.Combine(output, name + ".26G"), "nav glonass\n");
        return 0;
    }
}
