using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lazo
{
    /// <summary>Un sistema de coordenadas de la biblioteca de Autodesk (CS-MAP), con el código que entiende ReCap.</summary>
    internal sealed class CoordSystem
    {
        public string Key;
        public string Group;
        public string Unit;
        public string Description;
        public int Epsg;
        public bool Legacy { get { return Group == "LEGACY" || Description.StartsWith("Deprecated", StringComparison.OrdinalIgnoreCase); } }
        public string Label { get { return Description.Length > 0 ? Description : Key; } }
    }

    /// <summary>
    /// Biblioteca de sistemas de coordenadas que instalan los productos de Autodesk (ReCap, Civil 3D, Map) en
    /// %ProgramData%\Autodesk\Geospatial Coordinate Systems. ReCap solo acepta estos códigos: con uno que no existe
    /// ignora la reproyección sin avisar, así que Lazo solo ofrece los de aquí.
    /// Coordsys.CSD: 4 bytes de cabecera y registros de 728 bytes (clave, grupo, unidad, descripción, EPSG).
    /// </summary>
    internal static class CoordinateLibrary
    {
        private const int Record = 728;
        private static List<CoordSystem> _all;
        private static string _folder;

        /// <summary>Carpeta de la versión más reciente de la biblioteca, o null si no está instalada.</summary>
        public static string Folder
        {
            get
            {
                if (_folder != null) return _folder.Length == 0 ? null : _folder;
                _folder = "";
                try
                {
                    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk");
                    if (!Directory.Exists(root)) return null;
                    string best = Directory.GetDirectories(root, "Geospatial Coordinate Systems *")
                        .Where(d => File.Exists(Path.Combine(d, "Coordsys.CSD")))
                        .OrderByDescending(d => VersionOf(d)).FirstOrDefault();
                    if (best != null) _folder = best;
                }
                catch { }
                return _folder.Length == 0 ? null : _folder;
            }
        }

        private static Version VersionOf(string folder)
        {
            Version version;
            return Version.TryParse(Path.GetFileName(folder).Replace("Geospatial Coordinate Systems", "").Trim(), out version) ? version : new Version(0, 0);
        }

        public static IList<CoordSystem> All
        {
            get
            {
                if (_all == null) _all = Load(Folder == null ? null : Path.Combine(Folder, "Coordsys.CSD"));
                return _all;
            }
        }

        public static bool Available { get { return All.Count > 0; } }

        internal static List<CoordSystem> Load(string path)
        {
            List<CoordSystem> list = new List<CoordSystem>();
            try
            {
                if (path == null || !File.Exists(path)) return list;
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 4 + Record || (data.Length - 4) % Record != 0) return list;
                for (int start = 4; start + Record <= data.Length; start += Record)
                {
                    CoordSystem cs = new CoordSystem
                    {
                        Key = Text(data, start, 24),
                        Group = Text(data, start + 96, 24),
                        Unit = Text(data, start + 192, 16),
                        Description = Text(data, start + 584, 64),
                        Epsg = BitConverter.ToInt16(data, start + 724)
                    };
                    if (cs.Key.Length > 0) list.Add(cs);
                }
            }
            catch { list.Clear(); }
            return list;
        }

        private static string Text(byte[] data, int start, int length)
        {
            int end = start;
            while (end < start + length && data[end] != 0) end++;
            return Encoding.GetEncoding(1252).GetString(data, start, end - start).Trim();
        }

        public static CoordSystem Find(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            return All.FirstOrDefault(cs => string.Equals(cs.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>El sistema de Autodesk para un código EPSG; se prefieren los vigentes a los heredados.</summary>
        public static CoordSystem ByEpsg(int epsg)
        {
            if (epsg <= 0) return null;
            return All.Where(cs => cs.Epsg == epsg).OrderBy(cs => cs.Legacy ? 1 : 0).ThenBy(cs => cs.Key.Length).FirstOrDefault();
        }

        /// <summary>Busca por clave, descripción o EPSG (todas las palabras). Sin texto, nada.</summary>
        public static List<CoordSystem> Search(string query, int max)
        {
            string[] words = (query ?? "").Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return new List<CoordSystem>();
            return All.Where(cs => !cs.Legacy && words.All(w =>
                    cs.Key.ToLowerInvariant().Contains(w) || cs.Description.ToLowerInvariant().Contains(w) ||
                    (cs.Epsg > 0 && cs.Epsg.ToString(CultureInfo.InvariantCulture) == w.Replace("epsg:", ""))))
                .OrderBy(cs => cs.Epsg.ToString(CultureInfo.InvariantCulture) == words[0] ? 0 : 1)
                .ThenBy(cs => cs.Key.Length).Take(max).ToList();
        }
    }

    /// <summary>Sistema que declara una nube de puntos en su archivo.</summary>
    internal sealed class CloudCrs
    {
        public int Epsg;
        /// <summary>Nombre que trae el archivo (de su WKT), si lo trae.</summary>
        public string Name = "";
        /// <summary>El equivalente en la biblioteca de Autodesk, o null si ReCap no lo conoce.</summary>
        public CoordSystem Match;
    }

    /// <summary>
    /// Lee el sistema de coordenadas que declara un archivo LAS/LAZ (registros GeoKey o WKT, también los extendidos
    /// de LAS 1.4) o E57 (coordinateMetadata del XML). La cabecera y los registros de LAZ no van comprimidos.
    /// </summary>
    internal static class CloudCrsReader
    {
        public static CloudCrs Read(string path)
        {
            try
            {
                string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                CloudCrs crs = ext == ".e57" ? ReadE57(path) : ReadLas(path);
                if (crs != null) crs.Match = CoordinateLibrary.ByEpsg(crs.Epsg);
                return crs;
            }
            catch { return null; }
        }

        private static CloudCrs ReadLas(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (BinaryReader reader = new BinaryReader(file))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "LASF") return null;
                file.Position = 25;
                byte minor = reader.ReadByte();
                file.Position = 94;
                ushort headerSize = reader.ReadUInt16();
                file.Position = 100;
                uint vlrCount = reader.ReadUInt32();
                int geoEpsg = 0;
                string wkt = null;
                file.Position = headerSize;
                for (uint i = 0; i < vlrCount && file.Position + 54 <= file.Length; i++)
                {
                    reader.ReadUInt16();
                    string user = Encoding.ASCII.GetString(reader.ReadBytes(16)).TrimEnd('\0');
                    ushort id = reader.ReadUInt16();
                    ushort length = reader.ReadUInt16();
                    reader.ReadBytes(32);
                    long next = file.Position + length;
                    if (user == "LASF_Projection") Record(reader, id, length, ref geoEpsg, ref wkt);
                    file.Position = next;
                }
                // LAS 1.4: registros extendidos al final (allí suele ir el WKT).
                if (minor >= 4 && headerSize >= 247)
                {
                    file.Position = 235;
                    ulong start = reader.ReadUInt64();
                    uint count = reader.ReadUInt32();
                    if (start > 0 && start < (ulong)file.Length)
                    {
                        file.Position = (long)start;
                        for (uint i = 0; i < count && file.Position + 60 <= file.Length; i++)
                        {
                            reader.ReadUInt16();
                            string user = Encoding.ASCII.GetString(reader.ReadBytes(16)).TrimEnd('\0');
                            ushort id = reader.ReadUInt16();
                            ulong length = reader.ReadUInt64();
                            reader.ReadBytes(32);
                            long next = file.Position + (long)length;
                            if (user == "LASF_Projection" && length < 1 << 20) Record(reader, id, (int)length, ref geoEpsg, ref wkt);
                            file.Position = next;
                        }
                    }
                }
                CloudCrs fromWkt = FromText(wkt);
                if (fromWkt != null && fromWkt.Epsg > 0) return fromWkt;
                if (geoEpsg > 0) return new CloudCrs { Epsg = geoEpsg, Name = fromWkt != null ? fromWkt.Name : "" };
                return fromWkt;
            }
        }

        private static void Record(BinaryReader reader, ushort id, int length, ref int epsg, ref string wkt)
        {
            if (id == 2112) { wkt = Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0'); return; }
            if (id != 34735 || length < 8) return;
            // Directorio de GeoKeys: 4 valores de cabecera y luego claves de 4 valores (id, ubicación, cantidad, valor).
            ushort[] keys = new ushort[length / 2];
            for (int k = 0; k < keys.Length; k++) keys[k] = reader.ReadUInt16();
            int projected = 0, geographic = 0;
            for (int k = 4; k + 3 < keys.Length; k += 4)
            {
                if (keys[k + 1] != 0) continue;
                if (keys[k] == 3072) projected = keys[k + 3];
                if (keys[k] == 2048) geographic = keys[k + 3];
            }
            int code = projected > 0 && projected != 32767 ? projected : geographic > 0 && geographic != 32767 ? geographic : 0;
            if (code > 0) epsg = code;
        }

        private static CloudCrs ReadE57(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (BinaryReader reader = new BinaryReader(file))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "ASTM-E57") return null;
                reader.ReadUInt32(); reader.ReadUInt32();
                reader.ReadUInt64();
                ulong xmlOffset = reader.ReadUInt64();
                ulong xmlLength = reader.ReadUInt64();
                ulong page = reader.ReadUInt64();
                if (page < 64 || page > 1 << 20 || xmlLength > 64UL << 20) return null;
                // Páginas físicas con 4 bytes de CRC al final: el XML se lee saltándolos.
                int usable = (int)page - 4;
                byte[] xml = new byte[(int)xmlLength];
                int done = 0;
                long physical = (long)xmlOffset;
                while (done < xml.Length)
                {
                    long inPage = physical % (long)page;
                    if (inPage >= usable) { physical += (long)page - inPage; continue; }
                    file.Position = physical;
                    int take = (int)Math.Min(usable - inPage, xml.Length - done);
                    int read = file.Read(xml, done, take);
                    if (read <= 0) break;
                    done += read;
                    physical += read;
                }
                string text = Encoding.UTF8.GetString(xml, 0, done);
                Match meta = Regex.Match(text, @"<coordinateMetadata[^>]*>(.*?)</coordinateMetadata>", RegexOptions.Singleline);
                return meta.Success ? FromText(System.Net.WebUtility.HtmlDecode(meta.Groups[1].Value.Replace("<![CDATA[", "").Replace("]]>", ""))) : null;
            }
        }

        /// <summary>EPSG y nombre a partir de un WKT (1 o 2) o de un texto tipo «EPSG:3116».</summary>
        internal static CloudCrs FromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            CloudCrs crs = new CloudCrs();
            Match name = Regex.Match(text, @"^\s*(?:PROJCS|GEOGCS|COMPD_CS|PROJCRS|GEOGCRS|COMPOUNDCRS|GEODCRS)\s*\[\s*""([^""]+)""");
            if (name.Success) crs.Name = name.Groups[1].Value;
            // En un WKT compuesto (horizontal + vertical) vale el horizontal: el primer PROJCS/GEOGCS de dentro.
            string scope = text;
            if (Regex.IsMatch(text, @"^\s*(?:COMPD_CS|COMPOUNDCRS)"))
            {
                Match inner = Regex.Match(text.Substring(1), @"(?:PROJCS|PROJCRS|GEOGCS|GEOGCRS|GEODCRS)\s*\[");
                if (inner.Success)
                {
                    scope = Block(text, inner.Index + 1);
                    Match innerName = Regex.Match(scope, @"^\w+\s*\[\s*""([^""]+)""");
                    if (innerName.Success) crs.Name = innerName.Groups[1].Value;
                }
            }
            // El código del sistema es el AUTHORITY/ID que cierra su bloque: el último que aparece en él.
            MatchCollection ids = Regex.Matches(scope, @"(?:AUTHORITY\s*\[\s*""EPSG""\s*,\s*""?|ID\s*\[\s*""EPSG""\s*,\s*|EPSG:\s*)(\d+)");
            int epsg;
            if (ids.Count > 0 && int.TryParse(ids[ids.Count - 1].Groups[1].Value, out epsg)) crs.Epsg = epsg;
            return crs.Epsg > 0 || crs.Name.Length > 0 ? crs : null;
        }

        /// <summary>El bloque WKT que empieza en <paramref name="start"/>, hasta su corchete de cierre.</summary>
        private static string Block(string text, int start)
        {
            int depth = 0;
            bool quoted = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') quoted = !quoted;
                if (quoted) continue;
                if (c == '[') depth++;
                else if (c == ']' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            return text.Substring(start);
        }
    }
}
