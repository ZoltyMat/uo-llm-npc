using System;
using Server;

namespace Server.Custom.LLMNpc
{
    // Maps a mobile's position to the town it belongs to, so an NPC can know it
    // serves Britain versus Vesper. Nearest classic-city center wins when the
    // mobile stands reasonably close; otherwise we fall back to the named region,
    // then to the nearest city outright.
    public static class BritanniaGeography
    {
        private class City
        {
            public string Name;
            public int X;
            public int Y;

            public City(string name, int x, int y)
            {
                Name = name;
                X = x;
                Y = y;
            }
        }

        // Classic Britannia city centers (Trammel/Felucca share coordinates).
        private static readonly City[] m_Cities = new City[]
        {
            new City("Britain", 1496, 1629),
            new City("Trinsic", 1828, 2948),
            new City("Vesper", 2899, 676),
            new City("Minoc", 2470, 439),
            new City("Yew", 542, 985),
            new City("Moonglow", 4467, 1283),
            new City("Magincia", 3728, 2164),
            new City("Skara Brae", 596, 2138),
            new City("Cove", 2237, 1214),
            new City("Jhelom", 1417, 3821),
            new City("Nujel'm", 3770, 1300),
            new City("Ocllo", 3650, 2653),
            new City("Serpent's Hold", 2895, 3479),
            new City("Wind", 1380, 924),
            new City("Delucia", 5277, 3994),
            new City("Papua", 5720, 3150)
        };

        // ~200 tiles from a city center counts as "in" that town.
        private const int TownRadiusSq = 200 * 200;

        public static string TownOf(Mobile m)
        {
            if (m == null)
                return "Britannia";

            string nearest = null;
            int bestSq = int.MaxValue;

            if (m.Map != null && m.Map != Map.Internal)
            {
                Point3D p = m.Location;

                for (int i = 0; i < m_Cities.Length; i++)
                {
                    int dx = m_Cities[i].X - p.X;
                    int dy = m_Cities[i].Y - p.Y;
                    int sq = dx * dx + dy * dy;

                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        nearest = m_Cities[i].Name;
                    }
                }
            }

            if (nearest != null && bestSq <= TownRadiusSq)
                return nearest;

            Region r = m.Region;
            while (r != null)
            {
                if (!string.IsNullOrEmpty(r.Name))
                    return r.Name;

                r = r.Parent;
            }

            return nearest != null ? nearest : "Britannia";
        }

        // All classic city names, for the P16 denizen population director.
        public static string[] CityNames()
        {
            string[] names = new string[m_Cities.Length];

            for (int i = 0; i < m_Cities.Length; i++)
                names[i] = m_Cities[i].Name;

            return names;
        }

        // Center coordinates of a named city, for distance math (P15 favor
        // rewards scale with how far the parcel must travel). False for names
        // that aren't classic cities (regions, "Britannia").
        public static bool TryGetCityCenter(string name, out Point3D center)
        {
            center = Point3D.Zero;

            if (string.IsNullOrEmpty(name))
                return false;

            for (int i = 0; i < m_Cities.Length; i++)
            {
                if (m_Cities[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    center = new Point3D(m_Cities[i].X, m_Cities[i].Y, 0);
                    return true;
                }
            }

            return false;
        }

        // A random named city, optionally excluding one (used to give an NPC an
        // origin town that differs from where they now hold their post).
        public static string RandomCityExcept(string except)
        {
            for (int attempts = 0; attempts < 8; attempts++)
            {
                string c = m_Cities[Utility.Random(m_Cities.Length)].Name;

                if (except == null || !c.Equals(except, StringComparison.OrdinalIgnoreCase))
                    return c;
            }

            return "Britain";
        }

        // Picks a far city (not the NPC's current town) and a standable spot near its
        // center for a cross-continent journey. The NPC RECALLS to this point, so the
        // destination needn't be reachable on foot — island and Lost Lands cities are
        // fair game. Returns false only if no standable tile turned up near the chosen
        // center, in which case the caller simply tries again next decision window.
        public static bool PickCrossContinentDestination(Map map, string exceptTown, out Point3D dest, out string cityName)
        {
            dest = Point3D.Zero;
            cityName = null;

            if (map == null || map == Map.Internal)
                return false;

            City target = null;
            for (int attempts = 0; attempts < 12; attempts++)
            {
                City c = m_Cities[Utility.Random(m_Cities.Length)];

                if (exceptTown == null || !c.Name.Equals(exceptTown, StringComparison.OrdinalIgnoreCase))
                {
                    target = c;
                    break;
                }
            }

            if (target == null)
                target = m_Cities[Utility.Random(m_Cities.Length)];

            // Search outward from the city center for a tile a mobile can stand on.
            for (int i = 0; i < 24; i++)
            {
                int distOut = Utility.RandomMinMax(0, 20);
                double ang = Utility.RandomDouble() * Math.PI * 2.0;

                int x = target.X + (int)Math.Round(Math.Cos(ang) * distOut);
                int y = target.Y + (int)Math.Round(Math.Sin(ang) * distOut);

                int z = map.GetAverageZ(x, y);
                if (map.CanSpawnMobile(x, y, z))
                {
                    dest = new Point3D(x, y, z);
                    cityName = target.Name;
                    return true;
                }
            }

            // Last resort: the raw center, if it happens to be standable.
            int cz = map.GetAverageZ(target.X, target.Y);
            if (map.CanSpawnMobile(target.X, target.Y, cz))
            {
                dest = new Point3D(target.X, target.Y, cz);
                cityName = target.Name;
                return true;
            }

            return false;
        }
    }
}
