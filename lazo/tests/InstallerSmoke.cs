using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

internal static class InstallerSmoke
{
    private static void Main(string[] args)
    {
        Assembly setup = Assembly.LoadFile(Path.GetFullPath(args[0]));
        byte[] embedded;
        using (Stream resource = setup.GetManifestResourceStream("Lazo.Payload"))
        {
            if (resource == null) throw new Exception("El instalador no contiene la aplicación.");
            using (MemoryStream memory = new MemoryStream())
            {
                resource.CopyTo(memory);
                embedded = memory.ToArray();
            }
        }
        byte[] original = File.ReadAllBytes(args[1]);
        using (SHA256 sha = SHA256.Create())
        {
            if (!sha.ComputeHash(embedded).SequenceEqual(sha.ComputeHash(original)))
                throw new Exception("El ejecutable incluido difiere de la compilación.");
        }
        Type work = setup.GetType("LazoInstaller.InstallWork", true);
        MethodInfo shortcut = work.GetMethod("CreateShortcut", BindingFlags.NonPublic | BindingFlags.Static);
        string link = Path.Combine(Path.GetDirectoryName(args[1]), "InstallerSmoke.lnk");
        try
        {
            shortcut.Invoke(null, new object[] { link, Path.GetFullPath(args[1]) });
            if (!File.Exists(link)) throw new Exception("No se pudo crear el acceso directo.");
        }
        finally { if (File.Exists(link)) File.Delete(link); }
        Console.WriteLine("OK: instalador autónomo con Lazo.exe íntegro (" + embedded.Length + " bytes)");
    }
}
