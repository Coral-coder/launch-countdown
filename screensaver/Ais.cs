// Vessels, from aisstream.io's live stream. It needs a (free) key, entered in the settings;
// without one there are simply no vessels.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace LaunchCountdownSaver
{
    internal static partial class Store
    {
        public static volatile List<Plane> Vessels = new List<Plane>();
        public static volatile string VesselNote = "no AIS key set";

        private static void Ais()
        {
            var seen = new Dictionary<string, Plane>();
            while (true)
            {
                var key = Settings.AisKey; var c = Current;
                if (string.IsNullOrWhiteSpace(key)) { VesselNote = "no AIS key set"; Vessels = new List<Plane>(); Thread.Sleep(5000); continue; }
                if (!Settings.Vessels || c == null || !c.HasPad || (DateTime.UtcNow - TrafficWanted).TotalSeconds > 60) { Thread.Sleep(2000); continue; }
                try
                {
                    using (var ws = new ClientWebSocket())
                    {
                        var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10)).Token; // reconnect now and then, which also picks up a new pad
                        ws.ConnectAsync(new Uri("wss://stream.aisstream.io/v0/stream"), stop).Wait();
                        double dLat = 250 / 110.57, dLon = 250 / (111.32 * Math.Cos(c.Lat * Math.PI / 180));
                        string sub = "{\"APIKey\":\"" + key.Trim().Replace("\"", "") + "\",\"BoundingBoxes\":[[[" + F(c.Lat - dLat) + "," + F(c.Lon - dLon) + "],[" + F(c.Lat + dLat) + "," + F(c.Lon + dLon) + "]]]," +
                            "\"FilterMessageTypes\":[\"PositionReport\",\"StandardClassBPositionReport\",\"ShipStaticData\"]}";
                        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(sub)), WebSocketMessageType.Text, true, stop).Wait();
                        VesselNote = "listening…";
                        var buf = new byte[65536]; var text = new StringBuilder(); var lastPublish = DateTime.UtcNow;
                        while (ws.State == WebSocketState.Open && Current == c && Settings.AisKey == key)
                        {
                            var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), stop).Result;
                            if (r.MessageType == WebSocketMessageType.Close) break;
                            text.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                            if (!r.EndOfMessage) continue;
                            var m = J.Parse(text.ToString()); text.Clear();
                            if (m["error"].Str != null) { VesselNote = "AIS: " + m["error"].Str; break; }
                            var meta = m["MetaData"]; string id = meta["MMSI"].Str;
                            if (id == null) continue;
                            Plane v;
                            if (!seen.TryGetValue(id, out v)) seen[id] = v = new Plane { Id = "ais" + id, Sea = true, Name = id };
                            if (!string.IsNullOrWhiteSpace(meta["ShipName"].Str)) v.Name = meta["ShipName"].Str.Trim();
                            var type = m["MessageType"].Str; var body = m["Message"][type];
                            if (type == "ShipStaticData") v.ShipType = (int)body["Type"].N();
                            else
                            {
                                v.Lat = meta["latitude"].N(); v.Lon = meta["longitude"].N(); v.T = Unix(DateTime.UtcNow);
                                double sog = body["Sog"].N(), cog = body["Cog"].N();
                                v.Kt = sog >= 102 ? 0 : sog; v.Track = cog >= 360 ? body["TrueHeading"].N() % 360 : cog;
                            }
                            if ((DateTime.UtcNow - lastPublish).TotalSeconds < 3) continue;
                            lastPublish = DateTime.UtcNow; double now = Unix(DateTime.UtcNow);
                            foreach (var old in seen.Where(kv => now - kv.Value.T > 3600).Select(kv => kv.Key).ToList()) seen.Remove(old);
                            // copies, so the drawing thread never sees one half-updated
                            Vessels = seen.Values.Where(x => x.Lat != 0 || x.Lon != 0).Select(x => new Plane { Id = x.Id, Name = x.Name, Sea = true, ShipType = x.ShipType, Lat = x.Lat, Lon = x.Lon, Kt = x.Kt, Track = x.Track, T = x.T }).ToList();
                            VesselNote = ""; Version++;
                        }
                    }
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    if (!(inner is OperationCanceledException)) VesselNote = "AIS not connected: " + inner.Message;
                    Problem("Ais", e);
                }
                Thread.Sleep(5000);
            }
        }
    }
}
