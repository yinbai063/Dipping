namespace Dipping.WPF.Services
{
    public static class VisionCameraSessionHub
    {
        private static readonly Dictionary<string, VisionCameraClient> Sessions =
            new(StringComparer.OrdinalIgnoreCase);

        public static bool TryGet(string ip, out VisionCameraClient client)
        {
            if (Sessions.TryGetValue(ip, out client!))
                return true;
            client = null!;
            return false;
        }

        public static void Set(string ip, VisionCameraClient client)
        {
            if (Sessions.Remove(ip, out var old) && !ReferenceEquals(old, client))
                old.Dispose();
            Sessions[ip] = client;
        }

        public static void Remove(string ip)
        {
            if (Sessions.Remove(ip, out var old))
                old.Dispose();
        }

        public static void DisposeAll()
        {
            var clients = Sessions.Values.ToList();
            Sessions.Clear();
            foreach (var client in clients)
            {
                try { client.Dispose(); } catch { }
            }
        }
    }
}
