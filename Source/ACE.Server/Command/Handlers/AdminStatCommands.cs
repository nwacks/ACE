using System;
using System.Diagnostics;
using System.Linq;
using System.Text;

using log4net;

using ACE.Database;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.Managers;
using ACE.Server.Physics.Common;
using ACE.Server.Physics.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public static class AdminStatCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // allstats
        [CommandHandler("allstats", AccessLevel.Advocate, CommandHandlerFlag.None, 0, "Displays a summary of all server statistics and usage")]
        public static void HandleAllStats(Session session, params string[] parameters)
        {
            HandleServerStatus(session, parameters);

            HandleServerPerformance(session, parameters);

            HandleLandblockPerformance(session, parameters);

            DeveloperDatabaseCommands.HandleDatabaseQueueInfo(session, parameters);
        }

        // serverstatus
        [CommandHandler("serverstatus", AccessLevel.Advocate, CommandHandlerFlag.None, 0, "Displays a summary of server statistics and usage")]
        public static void HandleServerStatus(Session session, params string[] parameters)
        {
            // This is formatted very similarly to GDL.

            var sb = new StringBuilder();

            var proc = Process.GetCurrentProcess();

            sb.Append($"Server Status:{'\n'}");

            var runTime = DateTime.Now - proc.StartTime;
            sb.Append($"Server Runtime: {(int)runTime.TotalHours}h {runTime.Minutes}m {runTime.Seconds}s{'\n'}");

            sb.Append($"Total CPU Time: {(int)proc.TotalProcessorTime.TotalHours}h {proc.TotalProcessorTime.Minutes}m {proc.TotalProcessorTime.Seconds}s, Threads: {proc.Threads.Count}{'\n'}");

            // todo, add actual system memory used/avail
            sb.Append($"{(proc.PrivateMemorySize64 >> 20):N0} MB used{'\n'}");  // sb.Append($"{(proc.PrivateMemorySize64 >> 20)} MB used, xxxx / yyyy MB physical mem free.{'\n'}");

            sb.Append($"{NetworkManager.GetSessionCount():N0} connections, {PlayerManager.GetAllOnline().Count:N0} players online{'\n'}");
            sb.Append($"Total Accounts Created: {DatabaseManager.Authentication.GetAccountCount():N0}, Total Characters Created: {(PlayerManager.GetAllOffline().Count + PlayerManager.GetAllOnline().Count):N0}{'\n'}");

            // 330 active objects, 1931 total objects(16777216 buckets.)

            // todo, expand this
            var loadedLandblocks = LandblockManager.GetLoadedLandblocks();
            int dormantLandblocks = 0;
            int players = 0, creatures = 0, missiles = 0, other = 0, total = 0;
            foreach (var landblock in loadedLandblocks)
            {
                if (landblock.IsDormant)
                    dormantLandblocks++;

                foreach (var worldObject in landblock.GetAllWorldObjectsForDiagnostics())
                {
                    if (worldObject is Player)
                        players++;
                    else if (worldObject is Creature)
                        creatures++;
                    else if (worldObject.Missile ?? false)
                        missiles++;
                    else
                        other++;

                    total++;
                }
            }
            sb.Append($"Landblocks: {(loadedLandblocks.Count - dormantLandblocks):N0} active, {dormantLandblocks:N0} dormant - Players: {players:N0}, Creatures: {creatures:N0}, Missiles: {missiles:N0}, Other: {other:N0}, Total: {total:N0}.{'\n'}"); // 11 total blocks loaded. 11 active. 0 pending dormancy. 0 dormant. 314 unloaded.
            // 11 total blocks loaded. 11 active. 0 pending dormancy. 0 dormant. 314 unloaded.

            if (ServerPerformanceMonitor.IsRunning)
                sb.Append($"Server Performance Monitor - UpdateGameWorld ~5m {ServerPerformanceMonitor.GetMonitor5m(ServerPerformanceMonitor.MonitorType.UpdateGameWorld_Entire).AverageEventDuration:N3}, ~1h {ServerPerformanceMonitor.GetMonitor1h(ServerPerformanceMonitor.MonitorType.UpdateGameWorld_Entire).AverageEventDuration:N3} s{'\n'}");
            else
                sb.Append($"Server Performance Monitor - Not running. To start use /serverperformance start{'\n'}");

            sb.Append($"Physics Cache Counts - BSPCache: {BSPCache.Count:N0}, GfxObjCache: {GfxObjCache.Count:N0}, PolygonCache: {PolygonCache.Count:N0}, VertexCache: {VertexCache.Count:N0}{'\n'}");

            sb.Append($"Physics Landblocks Count - {LScape.LandblocksCount:N0}, Total Server Objects: {ObjectMaint.ServerObjects.Count:N0}{'\n'}");

            sb.Append($"World DB Cache Counts - Weenies: {DatabaseManager.World.GetWeenieCacheCount():N0}, LandblockInstances: {DatabaseManager.World.GetLandblockInstancesCacheCount():N0}, PointsOfInterest: {DatabaseManager.World.GetPointsOfInterestCacheCount():N0}, Cookbooks: {DatabaseManager.World.GetCookbookCacheCount():N0}, Spells: {DatabaseManager.World.GetSpellCacheCount():N0}, Encounters: {DatabaseManager.World.GetEncounterCacheCount():N0}, Events: {DatabaseManager.World.GetEventsCacheCount():N0}{'\n'}");
            sb.Append($"Shard DB Counts - Biotas: {DatabaseManager.Shard.GetBiotaCount():N0}{'\n'}");

            sb.Append($"Portal.dat has {DatManager.PortalDat.FileCache.Count:N0} files cached of {DatManager.PortalDat.AllFiles.Count:N0} total{'\n'}");
            sb.Append($"Cell.dat has {DatManager.CellDat.FileCache.Count:N0} files cached of {DatManager.CellDat.AllFiles.Count:N0} total{'\n'}");

            CommandHandlerHelper.WriteOutputInfo(session, $"{sb}");
        }

        // serverstatus
        [CommandHandler("serverperformance", AccessLevel.Advocate, CommandHandlerFlag.None, 0, "Displays a summary of server performance statistics")]
        public static void HandleServerPerformance(Session session, params string[] parameters)
        {
            if (parameters != null && parameters.Length == 1)
            {
                if (parameters[0].ToLower() == "start")
                {
                    ServerPerformanceMonitor.Start();
                    CommandHandlerHelper.WriteOutputInfo(session, "Server Performance Monitor started");
                    return;
                }

                if (parameters[0].ToLower() == "stop")
                {
                    ServerPerformanceMonitor.Stop();
                    CommandHandlerHelper.WriteOutputInfo(session, "Server Performance Monitor stopped");
                    return;
                }

                if (parameters[0].ToLower() == "reset")
                {
                    ServerPerformanceMonitor.Reset();
                    CommandHandlerHelper.WriteOutputInfo(session, "Server Performance Monitor reset");
                    return;
                }
            }

            if (!ServerPerformanceMonitor.IsRunning)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Server Performance Monitor not running. To start use /serverperformance start");
                return;
            }

            CommandHandlerHelper.WriteOutputInfo(session, ServerPerformanceMonitor.ToString());
        }

        [CommandHandler("landblockperformance", AccessLevel.Advocate, CommandHandlerFlag.None, 0, "Displays a summary of landblock performance statistics")]
        public static void HandleLandblockPerformance(Session session, params string[] parameters)
        {
            var sb = new StringBuilder();

            var loadedLandblocks = LandblockManager.GetLoadedLandblocks();

            // Filter out landblocks that haven't recorded at least 1000 events
            var sortedByAverage = loadedLandblocks.Where(r => r.Monitor1h.TotalEvents >= 1000).OrderByDescending(r => r.Monitor1h.AverageEventDuration).Take(10);

            sb.Append($"Most Busy Landblock - By Average{'\n'}");
            sb.Append($"~1h Hits   Avg  Long  Last  Tot - Location   Players  Creatures{'\n'}");

            foreach (var entry in sortedByAverage)
            {
                int players = 0, creatures = 0;
                foreach (var worldObject in entry.GetAllWorldObjectsForDiagnostics())
                {
                    if (worldObject is Player)
                        players++;
                    else if (worldObject is Creature)
                        creatures++;
                }

                sb.Append($"{entry.Monitor1h.TotalEvents.ToString().PadLeft(7)} {entry.Monitor1h.AverageEventDuration:N4} {entry.Monitor1h.LongestEvent:N3} {entry.Monitor1h.LastEvent:N3} {((int)entry.Monitor1h.TotalSeconds).ToString().PadLeft(4)} - " +
                    $"0x{entry.Id.Raw:X8} {players.ToString().PadLeft(7)}  {creatures.ToString().PadLeft(9)}{'\n'}");
            }

            var sortedByLong = loadedLandblocks.Where(r => r.Monitor1h.TotalEvents >= 1000).OrderByDescending(r => r.Monitor1h.LongestEvent).Take(10);

            sb.Append($"Most Busy Landblock - By Longest{'\n'}");
            sb.Append($"~1h Hits   Avg  Long  Last  Tot - Location   Players  Creatures{'\n'}");

            foreach (var entry in sortedByLong)
            {
                int players = 0, creatures = 0;
                foreach (var worldObject in entry.GetAllWorldObjectsForDiagnostics())
                {
                    if (worldObject is Player)
                        players++;
                    else if (worldObject is Creature)
                        creatures++;
                }

                sb.Append($"{entry.Monitor1h.TotalEvents.ToString().PadLeft(7)} {entry.Monitor1h.AverageEventDuration:N4} {entry.Monitor1h.LongestEvent:N3} {entry.Monitor1h.LastEvent:N3} {((int)entry.Monitor1h.TotalSeconds).ToString().PadLeft(4)} - " +
                          $"0x{entry.Id.Raw:X8} {players.ToString().PadLeft(7)}  {creatures.ToString().PadLeft(9)}{'\n'}");
            }

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString());
        }
    }
}
