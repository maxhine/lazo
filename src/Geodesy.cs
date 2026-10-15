using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lazo
{
    /// <summary>
    /// Sistema de coordenadas que Lazo sabe calcular por su cuenta: geográficas o Transversa de Mercator sobre GRS80.
    /// Sirve para lo que ReCap no trae, como MAGNA-SIRGAS 2018 / Origen Nacional. WGS84, SIRGAS, MAGNA-SIRGAS y
    /// MAGNA-SIRGAS 2018 se tratan como el mismo marco (sin desplazamiento de datum), igual que los .prj de iTwin Capture.
    /// </summary>
    internal sealed class LazoProjection
    {
        public string Id;
        public string Name;
        public int Epsg;
        /// <summary>Clave equivalente en la biblioteca de ReCap, si existe.</summary>
        public string RecapKey;
        public bool Geographic;
        public double Lat0, Lon0, K0 = 1, FalseEasting, FalseNorthing;
        /// <summary>Alturas ortométricas EGM2008 (EPSG 3855) en lugar de las que traiga la nube.</summary>
        public bool Egm2008;
        /// <summary>Se muestra arriba en la lista de destino (los .prj del equipo).</summary>
        public bool Featured;

        /// <summary>Sistemas conocidos. Los tres primeros son los .prj de iTwin Capture del equipo.</summary>
        public static readonly List<LazoProjection> All = new List<LazoProjection>
        {
            Tm("on-egm", "MAGNA-SIRGAS 2018 / Origen Nacional + EGM2008", 9377, null, 4.0, -73.0, 0.9992, 5000000, 2000000, true, true),
            Tm("on", "MAGNA-SIRGAS 2018 / Origen Nacional", 9377, null, 4.0, -73.0, 0.9992, 5000000, 2000000, false, true),
            Tm("3116-egm", "MAGNA-SIRGAS / Colombia Bogotá + EGM2008", 3116, "MAGNA.Columbia-Bogota", 4.596200416666666, -74.07750791666666, 1, 1000000, 1000000, true, true),
            Tm("3116", "MAGNA-SIRGAS / Colombia Bogotá", 3116, "MAGNA.Columbia-Bogota", 4.596200416666666, -74.07750791666666, 1, 1000000, 1000000, false, false),
            Tm("3115", "MAGNA-SIRGAS / Colombia Oeste", 3115, "MAGNA.Columbia-West", 4.596200416666666, -77.07750791666666, 1, 1000000, 1000000, false, false),
            Tm("3114", "MAGNA-SIRGAS / Colombia Oeste Oeste", 3114, "MAGNA.Columbia-FarWest", 4.596200416666666, -80.07750791666666, 1, 1000000, 1000000, false, false),
            Tm("3117", "MAGNA-SIRGAS / Colombia Este Central", 3117, "MAGNA.Columbia-EastCtrl", 4.596200416666666, -71.07750791666666, 1, 1000000, 1000000, false, false),
            Tm("3118", "MAGNA-SIRGAS / Colombia Este", 3118, "MAGNA.Columbia-East", 4.596200416666666, -68.07750791666666, 1, 1000000, 1000000, false, false),
            Tm("32617", "WGS 84 / UTM 17N", 32617, "UTM84-17N", 0, -81, 0.9996, 500000, 0, false, false),
            Tm("32618", "WGS 84 / UTM 18N", 32618, "UTM84-18N", 0, -75, 0.9996, 500000, 0, false, false),
            Tm("32619", "WGS 84 / UTM 19N", 32619, "UTM84-19N", 0, -69, 0.9996, 500000, 0, false, false),
            new LazoProjection { Id = "4686", Name = "MAGNA-SIRGAS geográficas", Epsg = 4686, RecapKey = "MarcoGNR.LL", Geographic = true },
            new LazoProjection { Id = "4326", Name = "WGS 84 geográficas", Epsg = 4326, RecapKey = "LL84", Geographic = true }
        };

        private static LazoProjection Tm(string id, string name, int epsg, string recap, double lat0, double lon0, double k0, double fe, double fn, bool egm, bool featured)
        {
            return new LazoProjection { Id = id, Name = name, Epsg = epsg, RecapKey = recap, Lat0 = lat0, Lon0 = lon0, K0 = k0, FalseEasting = fe, FalseNorthing = fn, Egm2008 = egm, Featured = featured };
        }

        public static LazoProjection ById(string id)
        {
            return All.FirstOrDefault(p => p.Id == id);
        }

        /// <summary>El sistema de Lazo para un EPSG (o un nombre de WKT como «Origen-Nacional»), sin alturas EGM2008.</summary>
        public static LazoProjection Detect(int epsg, string name)
        {
            if (!string.IsNullOrEmpty(name) && name.Replace('_', ' ').Replace('-', ' ').IndexOf("Origen Nacional", StringComparison.OrdinalIgnoreCase) >= 0) return ById("on");
            return All.FirstOrDefault(p => p.Epsg == epsg && !p.Egm2008);
        }

        // ── Transversa de Mercator (series de Krüger de 6.º orden, Karney 2011), elipsoide GRS80 ──

        private const double A = 6378137.0, F = 1 / 298.257222101;
        private static readonly double N = F / (2 - F), E = Math.Sqrt(F * (2 - F));
        private static readonly double Rect = A / (1 + N) * (1 + N * N / 4 + Math.Pow(N, 4) / 64 + Math.Pow(N, 6) / 256);
        private static readonly double[] Alpha =
        {
            N / 2 - 2 * N * N / 3 + 5 * Math.Pow(N, 3) / 16 + 41 * Math.Pow(N, 4) / 180 - 127 * Math.Pow(N, 5) / 288 + 7891 * Math.Pow(N, 6) / 37800,
            13 * N * N / 48 - 3 * Math.Pow(N, 3) / 5 + 557 * Math.Pow(N, 4) / 1440 + 281 * Math.Pow(N, 5) / 630 - 1983433 * Math.Pow(N, 6) / 1935360,
            61 * Math.Pow(N, 3) / 240 - 103 * Math.Pow(N, 4) / 140 + 15061 * Math.Pow(N, 5) / 26880 + 167603 * Math.Pow(N, 6) / 181440,
            49561 * Math.Pow(N, 4) / 161280 - 179 * Math.Pow(N, 5) / 168 + 6601661 * Math.Pow(N, 6) / 7257600,
            34729 * Math.Pow(N, 5) / 80640 - 3418889 * Math.Pow(N, 6) / 1995840,
            212378941 * Math.Pow(N, 6) / 319334400
        };
        private static readonly double[] Beta =
        {
            N / 2 - 2 * N * N / 3 + 37 * Math.Pow(N, 3) / 96 - Math.Pow(N, 4) / 360 - 81 * Math.Pow(N, 5) / 512 + 96199 * Math.Pow(N, 6) / 604800,
            N * N / 48 + Math.Pow(N, 3) / 15 - 437 * Math.Pow(N, 4) / 1440 + 46 * Math.Pow(N, 5) / 105 - 1118711 * Math.Pow(N, 6) / 3870720,
            17 * Math.Pow(N, 3) / 480 - 37 * Math.Pow(N, 4) / 840 - 209 * Math.Pow(N, 5) / 4480 + 5569 * Math.Pow(N, 6) / 90720,
            4397 * Math.Pow(N, 4) / 161280 - 11 * Math.Pow(N, 5) / 504 - 830251 * Math.Pow(N, 6) / 7257600,
            4583 * Math.Pow(N, 5) / 161280 - 108847 * Math.Pow(N, 6) / 3991680,
            20648693 * Math.Pow(N, 6) / 638668800
        };

        private static double Atanh(double x) { return 0.5 * Math.Log((1 + x) / (1 - x)); }
        private static double Asinh(double x) { return Math.Log(x + Math.Sqrt(x * x + 1)); }

        /// <summary>Tangente de la latitud conforme a partir de la tangente de la geodésica.</summary>
        private static double Conformal(double tau)
        {
            double sigma = Math.Sinh(E * Atanh(E * tau / Math.Sqrt(1 + tau * tau)));
            return tau * Math.Sqrt(1 + sigma * sigma) - sigma * Math.Sqrt(1 + tau * tau);
        }

        private static double Geodetic(double tauPrime)
        {
            double tau = tauPrime;
            for (int i = 0; i < 8; i++)
            {
                double conformal = Conformal(tau);
                double step = (tauPrime - conformal) / Math.Sqrt(1 + conformal * conformal) *
                    (1 + (1 - E * E) * tau * tau) / ((1 - E * E) * Math.Sqrt(1 + tau * tau));
                tau += step;
                if (Math.Abs(step) < 1e-14) break;
            }
            return tau;
        }

        /// <summary>ξ (norte) sin escalar para una latitud sobre el meridiano central.</summary>
        private static double Xi(double latRad, double dLonRad, out double eta)
        {
            double tauPrime = Conformal(Math.Tan(latRad));
            double xiP = Math.Atan2(tauPrime, Math.Cos(dLonRad));
            double etaP = Asinh(Math.Sin(dLonRad) / Math.Sqrt(tauPrime * tauPrime + Math.Cos(dLonRad) * Math.Cos(dLonRad)));
            double xi = xiP;
            eta = etaP;
            for (int j = 1; j <= 6; j++)
            {
                xi += Alpha[j - 1] * Math.Sin(2 * j * xiP) * Math.Cosh(2 * j * etaP);
                eta += Alpha[j - 1] * Math.Cos(2 * j * xiP) * Math.Sinh(2 * j * etaP);
            }
            return xi;
        }

        private double OriginXi
        {
            get { double eta; return Xi(Lat0 * Math.PI / 180, 0, out eta); }
        }

        /// <summary>Latitud y longitud (grados) a coordenadas de este sistema. En geográficas, x = longitud, y = latitud.</summary>
        public void Forward(double lat, double lon, out double x, out double y)
        {
            if (Geographic) { x = lon; y = lat; return; }
            double eta;
            double xi = Xi(lat * Math.PI / 180, (lon - Lon0) * Math.PI / 180, out eta);
            x = FalseEasting + K0 * Rect * eta;
            y = FalseNorthing + K0 * Rect * (xi - OriginXi);
        }

        public void Inverse(double x, double y, out double lat, out double lon)
        {
            if (Geographic) { lon = x; lat = y; return; }
            double xi = (y - FalseNorthing) / (K0 * Rect) + OriginXi;
            double eta = (x - FalseEasting) / (K0 * Rect);
            double xiP = xi, etaP = eta;
            for (int j = 1; j <= 6; j++)
            {
                xiP -= Beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
                etaP -= Beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            }
            double tauPrime = Math.Sin(xiP) / Math.Sqrt(Math.Sinh(etaP) * Math.Sinh(etaP) + Math.Cos(xiP) * Math.Cos(xiP));
            lat = Math.Atan(Geodetic(tauPrime)) * 180 / Math.PI;
            lon = Lon0 + Math.Atan2(Math.Sinh(etaP), Math.Cos(xiP)) * 180 / Math.PI;
        }
    }

    /// <summary>
    /// Ondulación del geoide EGM2008 desde una malla .gtx (la egm08_25.gtx de PROJ: 2,5′, big endian, de sur a norte).
    /// Se leen solo las filas que se usan, así que basta para nubes de una región.
    /// </summary>
    internal sealed class GeoidGrid : IDisposable
    {
        private readonly FileStream _file;
        private readonly double _lat0, _lon0, _dLat, _dLon;
        private readonly int _rows, _cols;
        private readonly Dictionary<int, float[]> _cache = new Dictionary<int, float[]>();

        public GeoidGrid(string path)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] header = new byte[40];
            if (_file.Read(header, 0, 40) != 40) throw new InvalidDataException("La malla del geoide está incompleta.");
            _lat0 = BigDouble(header, 0); _lon0 = BigDouble(header, 8); _dLat = BigDouble(header, 16); _dLon = BigDouble(header, 24);
            _rows = BigInt(header, 32); _cols = BigInt(header, 36);
            if (_rows < 2 || _cols < 2 || _dLat <= 0 || _dLon <= 0 || 40L + (long)_rows * _cols * 4 > _file.Length)
                throw new InvalidDataException("La malla del geoide no es un .gtx válido.");
        }

        /// <summary>Busca egm08_25.gtx donde suelen dejarlo iTwin Capture, PROJ y QGIS.</summary>
        public static string Find()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string[] candidates =
            {
                Path.Combine(documents, "Bentley", "iTwin Capture Modeler", "egm08_25.gtx"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "egm08_25.gtx"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "proj", "egm08_25.gtx"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QGIS", "QGIS3", "profiles", "default", "proj", "egm08_25.gtx")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Altura del geoide sobre el elipsoide (m), por interpolación bilineal.</summary>
        public double Undulation(double lat, double lon)
        {
            double row = (lat - _lat0) / _dLat;
            double col = (lon - _lon0) / _dLon;
            while (col < 0) col += _cols;
            while (col >= _cols) col -= _cols;
            int r0 = Math.Max(0, Math.Min(_rows - 2, (int)Math.Floor(row)));
            int c0 = (int)Math.Floor(col);
            int c1 = (c0 + 1) % _cols;
            double fr = Math.Max(0, Math.Min(1, row - r0)), fc = col - c0;
            float[] low = Row(r0), high = Row(r0 + 1);
            double bottom = low[c0] * (1 - fc) + low[c1] * fc;
            double top = high[c0] * (1 - fc) + high[c1] * fc;
            return bottom * (1 - fr) + top * fr;
        }

        private float[] Row(int row)
        {
            float[] values;
            if (_cache.TryGetValue(row, out values)) return values;
            byte[] raw = new byte[_cols * 4];
            _file.Position = 40L + (long)row * _cols * 4;
            int done = 0;
            while (done < raw.Length) { int read = _file.Read(raw, done, raw.Length - done); if (read <= 0) break; done += read; }
            values = new float[_cols];
            for (int i = 0; i < _cols; i++)
            {
                byte[] value = { raw[i * 4 + 3], raw[i * 4 + 2], raw[i * 4 + 1], raw[i * 4] };
                values[i] = BitConverter.ToSingle(value, 0);
            }
            if (_cache.Count > 64) _cache.Clear();
            _cache[row] = values;
            return values;
        }

        private static double BigDouble(byte[] data, int at)
        {
            byte[] value = new byte[8];
            for (int i = 0; i < 8; i++) value[i] = data[at + 7 - i];
            return BitConverter.ToDouble(value, 0);
        }

        private static int BigInt(byte[] data, int at)
        {
            return (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
        }

        public void Dispose()
        {
            _file.Dispose();
        }
    }
}
