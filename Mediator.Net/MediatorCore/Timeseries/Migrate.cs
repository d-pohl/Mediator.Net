﻿using System;
using System.Linq;
using System.Diagnostics;

namespace Ifak.Fast.Mediator.Timeseries
{
    public class Migrate
    {
        public static void CopyData(string srcType, string srcConnectionString, string dstType, string dstConnectionString) {
            try {
                TimeSeriesDB src = OpenDatabase(srcType, srcConnectionString);
                TimeSeriesDB dst = OpenDatabase(dstType, dstConnectionString);
                CopyDatabase(source: src, dest: dst);
            }
            catch (Exception exp) {
                Console.Error.WriteLine(exp.Message);
                Console.Error.WriteLine(exp.StackTrace);
            }
        }

        private static TimeSeriesDB OpenDatabase(string type, string connectionString) {
            switch(type) {
                case "SQLite": {
                        var db = new SQLite.SQLiteTimeseriesDB();
                        db.Open("SQLite", connectionString: connectionString);
                        return db;
                    }
                case "Postgres": {
                        var db = new Postgres.PostgresTimeseriesDB();
                        db.Open("Postgres", connectionString: connectionString);
                        return db;
                    }
                default: throw new Exception("Unknown database type: " + type);
            }
        }

        public static void CopyDatabase(TimeSeriesDB source, TimeSeriesDB dest) {

            ChannelInfo[] sourceChannels = source.GetAllChannels();

            Console.WriteLine($"CopyDatabase source db channel count: {sourceChannels.Length}.");

            double Total = sourceChannels.Length;
            double counter = 0;

            foreach (ChannelInfo ch in sourceChannels) {

                counter += 1;

                if (dest.ExistsChannel(ch.Object, ch.Variable)) {

                    Channel srcChannel = source.GetChannel(ch.Object, ch.Variable);
                    Channel dstChannel = dest.GetChannel(ch.Object, ch.Variable);

                    var sw = Stopwatch.StartNew();
                    long count = CopyChannel(srcChannel, dstChannel);
                    sw.Stop();
                    string progress = string.Format("{0:0.0}%", 100.0 * counter / Total);
                    Console.WriteLine($"Copied {count} entries of channel {ch.Object} in {sw.ElapsedMilliseconds} ms ({progress})");
                }
            }
        }

        private static long CopyChannel(Channel srcChannel, Channel dstChannel) {
            long count = 0;
            Timestamp t = Timestamp.Empty;
            while (true) {
                var vttqs = srcChannel.ReadData(t, Timestamp.Max, 6000, BoundingMethod.TakeFirstN, QualityFilter.ExcludeNone);
                if (vttqs.Count == 0) return count;
                count += vttqs.Count;
                var vtqs = vttqs.Select(VTQ.Make).ToArray();
                dstChannel.Upsert(vtqs);
                t = vttqs.Last().T.AddMillis(1);
            }
        }
    }
}
