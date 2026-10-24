using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QPX1200SPController.Models;

namespace QPX1200SPController.Services
{
    /// <summary>
    /// SQLite storage (powerlog.db next to the executable).
    /// Samples and activity records are queued in memory and written in batches by a
    /// background flush, so a slow or locked database never blocks polling. Rows that
    /// fail to write stay queued and are retried.
    /// </summary>
    public sealed class DatabaseService : IDisposable
    {
        public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
        public const int MaxRowsPerQuery = 200000;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RetentionCheckInterval = TimeSpan.FromHours(6);
        private const int SchemaVersion = 2;
        private const int MaxUnwrittenSamples = 200000;
        private const int MaxUnwrittenActivities = 20000;

        /// <summary>A sample further than this from the previous one starts a new run (link down, app closed).</summary>
        public const double MaxGapSeconds = 10;

        private const double MinutesPerDay = 1440;

        // Ranges longer than this are plotted and summarised from the per-minute rollup table.
        private static readonly TimeSpan RollupThreshold = TimeSpan.FromHours(36);

        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly ConcurrentQueue<PowerSupplyStatus> _sampleQueue = new ConcurrentQueue<PowerSupplyStatus>();
        private readonly ConcurrentQueue<ActivityRecord> _activityQueue = new ConcurrentQueue<ActivityRecord>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly Timer _flushTimer;

        // Only touched while holding _writeLock.
        private readonly List<PowerSupplyStatus> _unwrittenSamples = new List<PowerSupplyStatus>();
        private readonly List<ActivityRecord> _unwrittenActivities = new List<ActivityRecord>();
        private bool _initialized;
        private bool _writeFailing;
        private double _lastSampleT = double.NaN; // OLE date of the newest written sample (for Dt)
        private DateTime _nextRetentionRunUtc = DateTime.MinValue;

        public DatabaseService()
        {
            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "powerlog.db");
            // Default Timeout caps System.Data.SQLite's own busy retries (30 s by default) so a locked
            // database fails fast and the rows are simply retried on the next flush.
            _connectionString = $"Data Source={_dbPath};Version=3;Default Timeout=5;";

            TryInitialize();
            _flushTimer = new Timer(_ => { var __ = FlushAsync(force: false); }, null, FlushInterval, FlushInterval);
        }

        public string DatabasePath => _dbPath;

        /// <summary>True while writes are failing (rows are kept in memory and retried).</summary>
        public bool IsWriteFailing => _writeFailing;

        /// <summary>False if the database could not be opened or created (nothing is being logged).</summary>
        public bool IsAvailable => _initialized;

        #region Schema

        private SQLiteConnection Open()
        {
            var connection = new SQLiteConnection(_connectionString);
            try
            {
                connection.Open();
                Execute(connection, "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;");
                return connection;
            }
            catch
            {
                // Release the file handle, otherwise a damaged file cannot be set aside.
                connection.Dispose();
                throw;
            }
        }

        private bool TryInitialize()
        {
            try
            {
                // The app is always installed fresh, so there is no schema upgrade path: a database
                // file with any other layout (or a damaged file) is set aside and a new one started.
                if (File.Exists(_dbPath) && !HasCurrentSchema())
                    SetAsideIncompatibleDatabase();

                if (!File.Exists(_dbPath))
                    SQLiteConnection.CreateFile(_dbPath);

                using (var connection = Open())
                {
                    // WAL lets the Data Logs/plots read while samples are being written.
                    Execute(connection, "PRAGMA journal_mode = WAL;");

                    using (var transaction = connection.BeginTransaction())
                    {
                        Execute(connection, @"
                            -- T is the local time as an OLE date (days): fast range/bucket arithmetic and
                            -- directly usable as plot X. Timestamp is the same time, human readable.
                            CREATE TABLE IF NOT EXISTS PowerLogs (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                T REAL NOT NULL,
                                Timestamp TEXT NOT NULL,
                                Voltage REAL NOT NULL,
                                Current REAL NOT NULL,
                                Power REAL NOT NULL,
                                OutputEnabled INTEGER NOT NULL,
                                CvMode INTEGER NOT NULL,
                                CcMode INTEGER NOT NULL,
                                UnregulatedMode INTEGER NOT NULL,
                                OvpTrip INTEGER NOT NULL,
                                OcpTrip INTEGER NOT NULL,
                                OutputSenseTrip INTEGER NOT NULL,
                                FaultTrip INTEGER NOT NULL,
                                Dt REAL NOT NULL  -- seconds since the previous sample, 0 after a gap
                            );
                            CREATE INDEX IF NOT EXISTS IX_PowerLogs_T ON PowerLogs (T);

                            -- One row per minute (M = floor(T * 1440)), maintained on every write, so long
                            -- ranges are plotted and summarised without scanning millions of samples.
                            -- E/Q/OnS are sums of Power*Dt, Current*Dt and Dt while the output was on.
                            CREATE TABLE IF NOT EXISTS PowerLogsMinute (
                                M INTEGER PRIMARY KEY,
                                N INTEGER NOT NULL,
                                TFirst REAL NOT NULL, TLast REAL NOT NULL,
                                VMin REAL NOT NULL, VMax REAL NOT NULL, VSum REAL NOT NULL,
                                IMin REAL NOT NULL, IMax REAL NOT NULL, ISum REAL NOT NULL,
                                PMin REAL NOT NULL, PMax REAL NOT NULL, PSum REAL NOT NULL,
                                E REAL NOT NULL, Q REAL NOT NULL, OnS REAL NOT NULL
                            );

                            CREATE TABLE IF NOT EXISTS CommandLog (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                T REAL NOT NULL,
                                Timestamp TEXT NOT NULL,
                                Kind TEXT NOT NULL,
                                Source TEXT,
                                Command TEXT,
                                Response TEXT,
                                Result TEXT,
                                Requested REAL,
                                Readback REAL,
                                Detail TEXT,
                                DurationMs REAL
                            );
                            CREATE INDEX IF NOT EXISTS IX_CommandLog_T ON CommandLog (T);");
                        Execute(connection, $"PRAGMA user_version = {SchemaVersion};");
                        transaction.Commit();
                    }

                    object last = Scalar(connection, "SELECT MAX(T) FROM PowerLogs;");
                    _lastSampleT = last is DBNull || last == null ? double.NaN : Convert.ToDouble(last, Inv);
                }

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                _initialized = false;
                LoggingService.Instance.LogException(ex, nameof(DatabaseService), nameof(TryInitialize));
                return false;
            }
        }

        /// <summary>True for a database created by this version, or an empty file.</summary>
        private bool HasCurrentSchema()
        {
            try
            {
                using (var connection = Open())
                {
                    if (Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), Inv) == SchemaVersion)
                        return true;
                    return Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';"), Inv) == 0;
                }
            }
            catch
            {
                return false; // not a readable SQLite database
            }
        }

        private void SetAsideIncompatibleDatabase()
        {
            SQLiteConnection.ClearAllPools();
            string target = Path.Combine(Path.GetDirectoryName(_dbPath),
                $"powerlog_old_{DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv)}.db");

            File.Move(_dbPath, target);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(_dbPath + suffix))
                    File.Move(_dbPath + suffix, target + suffix);
            }

            LoggingService.Instance.LogWarning(
                $"powerlog.db was not created by this version of the app. It was renamed to {Path.GetFileName(target)} and a new database was started.",
                nameof(DatabaseService), nameof(SetAsideIncompatibleDatabase));
        }

        #endregion

        #region Writing

        /// <summary>Queues a measurement for the next batch write.</summary>
        public void EnqueueSample(PowerSupplyStatus status)
        {
            if (status != null)
                _sampleQueue.Enqueue(status);
        }

        /// <summary>Queues a command/connection/error record for the next batch write.</summary>
        public void EnqueueActivity(ActivityRecord record)
        {
            if (record != null)
                _activityQueue.Enqueue(record);
        }

        /// <summary>
        /// Writes queued rows. <paramref name="force"/> also writes command records still
        /// waiting for their read-back, and waits for a flush already in progress (used on exit).
        /// </summary>
        public async Task FlushAsync(bool force)
        {
            if (!await _writeLock.WaitAsync(force ? 15000 : 0))
                return;
            try
            {
                await Task.Run(() => FlushLocked(force));
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(DatabaseService), nameof(FlushAsync));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private void FlushLocked(bool force)
        {
            while (_sampleQueue.TryDequeue(out var sample))
                _unwrittenSamples.Add(sample);
            while (_activityQueue.TryDequeue(out var activity))
                _unwrittenActivities.Add(activity);
            TrimUnwritten();

            var activities = force
                ? _unwrittenActivities.ToList()
                : _unwrittenActivities.Where(a => a.IsReadyToWrite).ToList();

            if (_unwrittenSamples.Count > 0 || activities.Count > 0)
            {
                if (!_initialized && !TryInitialize())
                    return;

                try
                {
                    WriteBatch(_unwrittenSamples, activities);
                    _unwrittenSamples.Clear();
                    _unwrittenActivities.RemoveAll(activities.Contains);

                    if (_writeFailing)
                    {
                        _writeFailing = false;
                        LoggingService.Instance.LogInfo("Database writes recovered.", nameof(DatabaseService), nameof(FlushLocked));
                    }
                }
                catch (Exception ex)
                {
                    // Keep the rows and retry on the next flush; only log the first failure of a streak.
                    if (!_writeFailing)
                    {
                        _writeFailing = true;
                        LoggingService.Instance.LogError(
                            $"Database write failed, rows are kept in memory and retried: {ex.Message}",
                            nameof(DatabaseService), nameof(FlushLocked));
                    }
                    return;
                }
            }

            RunRetentionIfDue();
        }

        private void TrimUnwritten()
        {
            int excessSamples = _unwrittenSamples.Count - MaxUnwrittenSamples;
            if (excessSamples > 0)
            {
                _unwrittenSamples.RemoveRange(0, excessSamples);
                LoggingService.Instance.LogError($"Database unavailable: dropped {excessSamples} oldest unwritten samples.",
                    nameof(DatabaseService), nameof(TrimUnwritten));
            }

            int excessActivities = _unwrittenActivities.Count - MaxUnwrittenActivities;
            if (excessActivities > 0)
                _unwrittenActivities.RemoveRange(0, excessActivities);
        }

        private void WriteBatch(IReadOnlyList<PowerSupplyStatus> samples, IReadOnlyList<ActivityRecord> activities)
        {
            double lastT = _lastSampleT;
            var minutes = new MinuteRollup();

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                if (samples.Count > 0)
                {
                    using (var command = new SQLiteCommand(@"
                        INSERT INTO PowerLogs (T, Timestamp, Voltage, Current, Power, OutputEnabled,
                            CvMode, CcMode, UnregulatedMode, OvpTrip, OcpTrip, OutputSenseTrip, FaultTrip, Dt)
                        VALUES (@T, @Timestamp, @Voltage, @Current, @Power, @OutputEnabled,
                            @CvMode, @CcMode, @UnregulatedMode, @OvpTrip, @OcpTrip, @OutputSenseTrip, @FaultTrip, @Dt);",
                        connection, transaction))
                    {
                        var p = command.Parameters;
                        foreach (var name in new[] { "@T", "@Timestamp", "@Voltage", "@Current", "@Power", "@OutputEnabled", "@CvMode",
                                     "@CcMode", "@UnregulatedMode", "@OvpTrip", "@OcpTrip", "@OutputSenseTrip", "@FaultTrip", "@Dt" })
                            p.Add(new SQLiteParameter(name));

                        foreach (var s in samples)
                        {
                            var e = s.EventStatus ?? new EventStatus();
                            var time = s.Timestamp == default(DateTime) ? DateTime.Now : s.Timestamp;
                            double t = time.ToOADate();
                            double dt = double.IsNaN(lastT) ? 0 : (t - lastT) * 86400.0;
                            if (dt <= 0 || dt > MaxGapSeconds)
                                dt = 0; // first sample, clock change or a gap: nothing to integrate
                            lastT = Math.Max(double.IsNaN(lastT) ? t : lastT, t);
                            minutes.Add(t, s.Voltage, s.Current, s.Power, s.OutputEnabled, dt);

                            p["@T"].Value = t;
                            p["@Dt"].Value = dt;
                            p["@Timestamp"].Value = FormatTimestamp(time);
                            p["@Voltage"].Value = s.Voltage;
                            p["@Current"].Value = s.Current;
                            p["@Power"].Value = s.Power;
                            p["@OutputEnabled"].Value = s.OutputEnabled ? 1 : 0;
                            p["@CvMode"].Value = e.ConstantVoltageMode;
                            p["@CcMode"].Value = e.ConstantCurrentMode;
                            p["@UnregulatedMode"].Value = e.UnregulatedMode;
                            p["@OvpTrip"].Value = e.OverVoltageTrip;
                            p["@OcpTrip"].Value = e.OverCurrentTrip;
                            p["@OutputSenseTrip"].Value = e.OutputSenseTrip;
                            p["@FaultTrip"].Value = e.FaultTrip;
                            command.ExecuteNonQuery();
                        }
                    }

                    minutes.Write(connection, transaction);
                }

                if (activities.Count > 0)
                {
                    using (var command = new SQLiteCommand(@"
                        INSERT INTO CommandLog (T, Timestamp, Kind, Source, Command, Response, Result, Requested, Readback, Detail, DurationMs)
                        VALUES (@T, @Timestamp, @Kind, @Source, @Command, @Response, @Result, @Requested, @Readback, @Detail, @DurationMs);
                        SELECT last_insert_rowid();",
                        connection, transaction))
                    {
                        var p = command.Parameters;
                        foreach (var name in new[] { "@T", "@Timestamp", "@Kind", "@Source", "@Command", "@Response", "@Result",
                                     "@Requested", "@Readback", "@Detail", "@DurationMs" })
                            p.Add(new SQLiteParameter(name));

                        foreach (var a in activities)
                        {
                            p["@T"].Value = a.Timestamp.ToOADate();
                            p["@Timestamp"].Value = FormatTimestamp(a.Timestamp);
                            p["@Kind"].Value = a.Kind;
                            p["@Source"].Value = (object)a.Source ?? DBNull.Value;
                            p["@Command"].Value = (object)a.Command ?? DBNull.Value;
                            p["@Response"].Value = (object)a.Response ?? DBNull.Value;
                            p["@Result"].Value = (object)a.Result ?? DBNull.Value;
                            p["@Requested"].Value = (object)a.Requested ?? DBNull.Value;
                            p["@Readback"].Value = (object)a.Readback ?? DBNull.Value;
                            p["@Detail"].Value = (object)a.Detail ?? DBNull.Value;
                            p["@DurationMs"].Value = (object)a.DurationMs ?? DBNull.Value;
                            a.Id = Convert.ToInt64(command.ExecuteScalar(), Inv);
                        }
                    }
                }

                transaction.Commit();
            }

            _lastSampleT = lastT; // only advance once the batch is really stored
        }

        /// <summary>Per-minute aggregates of one write batch, merged into PowerLogsMinute with an upsert.</summary>
        private sealed class MinuteRollup
        {
            private readonly Dictionary<long, Aggregate> _minutes = new Dictionary<long, Aggregate>();

            public void Add(double t, double v, double i, double p, bool on, double dt)
            {
                long m = (long)Math.Floor(t * MinutesPerDay);
                if (!_minutes.TryGetValue(m, out var agg))
                    _minutes[m] = agg = new Aggregate();
                agg.AddSample(t, v, i, p, on, dt);
            }

            public void Write(SQLiteConnection connection, SQLiteTransaction transaction)
            {
                if (_minutes.Count == 0)
                    return;

                using (var command = new SQLiteCommand(@"
                    INSERT INTO PowerLogsMinute (M, N, TFirst, TLast, VMin, VMax, VSum, IMin, IMax, ISum, PMin, PMax, PSum, E, Q, OnS)
                    VALUES (@M, @N, @TFirst, @TLast, @VMin, @VMax, @VSum, @IMin, @IMax, @ISum, @PMin, @PMax, @PSum, @E, @Q, @OnS)
                    ON CONFLICT (M) DO UPDATE SET
                        N = N + excluded.N,
                        TFirst = MIN(TFirst, excluded.TFirst), TLast = MAX(TLast, excluded.TLast),
                        VMin = MIN(VMin, excluded.VMin), VMax = MAX(VMax, excluded.VMax), VSum = VSum + excluded.VSum,
                        IMin = MIN(IMin, excluded.IMin), IMax = MAX(IMax, excluded.IMax), ISum = ISum + excluded.ISum,
                        PMin = MIN(PMin, excluded.PMin), PMax = MAX(PMax, excluded.PMax), PSum = PSum + excluded.PSum,
                        E = E + excluded.E, Q = Q + excluded.Q, OnS = OnS + excluded.OnS;",
                    connection, transaction))
                {
                    foreach (var pair in _minutes)
                    {
                        var a = pair.Value;
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@M", pair.Key);
                        command.Parameters.AddWithValue("@N", a.Count);
                        command.Parameters.AddWithValue("@TFirst", a.First);
                        command.Parameters.AddWithValue("@TLast", a.Last);
                        command.Parameters.AddWithValue("@VMin", a.VMin);
                        command.Parameters.AddWithValue("@VMax", a.VMax);
                        command.Parameters.AddWithValue("@VSum", a.VSum);
                        command.Parameters.AddWithValue("@IMin", a.IMin);
                        command.Parameters.AddWithValue("@IMax", a.IMax);
                        command.Parameters.AddWithValue("@ISum", a.ISum);
                        command.Parameters.AddWithValue("@PMin", a.PMin);
                        command.Parameters.AddWithValue("@PMax", a.PMax);
                        command.Parameters.AddWithValue("@PSum", a.PSum);
                        command.Parameters.AddWithValue("@E", a.EnergyWs);
                        command.Parameters.AddWithValue("@Q", a.ChargeAs);
                        command.Parameters.AddWithValue("@OnS", a.OnSeconds);
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>Running count/min/max/sum of V, I, P plus the time integrals; mergeable.</summary>
        private sealed class Aggregate
        {
            public long Count;
            public double First = double.PositiveInfinity, Last = double.NegativeInfinity;
            public double VMin = double.PositiveInfinity, VMax = double.NegativeInfinity, VSum;
            public double IMin = double.PositiveInfinity, IMax = double.NegativeInfinity, ISum;
            public double PMin = double.PositiveInfinity, PMax = double.NegativeInfinity, PSum;
            public double EnergyWs, ChargeAs, OnSeconds;

            public void AddSample(double t, double v, double i, double p, bool on, double dt)
            {
                Count++;
                First = Math.Min(First, t); Last = Math.Max(Last, t);
                VMin = Math.Min(VMin, v); VMax = Math.Max(VMax, v); VSum += v;
                IMin = Math.Min(IMin, i); IMax = Math.Max(IMax, i); ISum += i;
                PMin = Math.Min(PMin, p); PMax = Math.Max(PMax, p); PSum += p;
                EnergyWs += p * dt; ChargeAs += i * dt;
                if (on)
                    OnSeconds += dt;
            }

            /// <summary>Merges one row of: N, TFirst, TLast, VMin, VMax, VSum, IMin, IMax, ISum, PMin, PMax, PSum, E, Q, OnS.</summary>
            public void Merge(SQLiteDataReader r)
            {
                if (r.IsDBNull(0) || r.GetInt64(0) == 0)
                    return;
                Count += r.GetInt64(0);
                First = Math.Min(First, r.GetDouble(1)); Last = Math.Max(Last, r.GetDouble(2));
                VMin = Math.Min(VMin, r.GetDouble(3)); VMax = Math.Max(VMax, r.GetDouble(4)); VSum += r.GetDouble(5);
                IMin = Math.Min(IMin, r.GetDouble(6)); IMax = Math.Max(IMax, r.GetDouble(7)); ISum += r.GetDouble(8);
                PMin = Math.Min(PMin, r.GetDouble(9)); PMax = Math.Max(PMax, r.GetDouble(10)); PSum += r.GetDouble(11);
                EnergyWs += r.GetDouble(12); ChargeAs += r.GetDouble(13); OnSeconds += r.GetDouble(14);
            }

            public HistoryStats ToStats()
            {
                var s = new HistoryStats { Samples = Count };
                if (Count == 0)
                    return s;
                s.First = DateTime.FromOADate(First);
                s.Last = DateTime.FromOADate(Last);
                s.VoltageMin = VMin; s.VoltageMax = VMax; s.VoltageAvg = VSum / Count;
                s.CurrentMin = IMin; s.CurrentMax = IMax; s.CurrentAvg = ISum / Count;
                s.PowerMin = PMin; s.PowerMax = PMax; s.PowerAvg = PSum / Count;
                s.EnergyWh = EnergyWs / 3600.0;
                s.ChargeAh = ChargeAs / 3600.0;
                s.OutputOnSeconds = OnSeconds;
                return s;
            }
        }

        private void RunRetentionIfDue()
        {
            int days = Settings.Current.RetentionDays;
            if (days <= 0 || DateTime.UtcNow < _nextRetentionRunUtc)
                return;
            _nextRetentionRunUtc = DateTime.UtcNow + RetentionCheckInterval;

            try
            {
                var cutoff = DateTime.Now.AddDays(-days);
                int samples, activities;
                using (var connection = Open())
                {
                    samples = Execute(connection, "DELETE FROM PowerLogs WHERE T < @Cutoff;", cutoff.ToOADate());
                    activities = Execute(connection, "DELETE FROM CommandLog WHERE T < @Cutoff;", cutoff.ToOADate());
                    Execute(connection, "DELETE FROM PowerLogsMinute WHERE M < @Cutoff;", Math.Floor(cutoff.ToOADate() * MinutesPerDay));
                }
                if (samples + activities > 0)
                    LoggingService.Instance.LogInfo(
                        $"Retention ({days} days): deleted {samples} samples and {activities} log rows older than {FormatTimestamp(cutoff)}.",
                        nameof(DatabaseService), nameof(RunRetentionIfDue));
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(DatabaseService), nameof(RunRetentionIfDue));
            }
        }

        #endregion

        #region Reading

        public async Task<long> CountLogsAsync(DateTime startTime, DateTime endTime)
        {
            return await Task.Run(() =>
            {
                using (var connection = Open())
                using (var command = new SQLiteCommand(
                    "SELECT COUNT(*) FROM PowerLogs WHERE T BETWEEN @T0 AND @T1;", connection))
                {
                    AddRange(command, startTime, endTime);
                    return Convert.ToInt64(command.ExecuteScalar(), Inv);
                }
            });
        }

        private const string LogColumns = @"Timestamp, Voltage, Current, Power, OutputEnabled, CvMode, CcMode, UnregulatedMode,
                           OvpTrip, OcpTrip, OutputSenseTrip, FaultTrip";

        /// <summary>Returns one page of samples in the range, oldest first.</summary>
        public async Task<List<LogEntry>> GetLogsAsync(DateTime startTime, DateTime endTime, int maxRows = MaxRowsPerQuery, long offset = 0)
        {
            return await Task.Run(() =>
            {
                var logs = new List<LogEntry>();
                using (var connection = Open())
                using (var command = new SQLiteCommand($@"
                    SELECT {LogColumns}
                    FROM PowerLogs
                    WHERE T BETWEEN @T0 AND @T1
                    ORDER BY T ASC
                    LIMIT @MaxRows OFFSET @Offset;", connection))
                {
                    AddRange(command, startTime, endTime);
                    command.Parameters.AddWithValue("@MaxRows", maxRows);
                    command.Parameters.AddWithValue("@Offset", offset);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var entry = ReadLogEntry(reader);
                            if (entry != null)
                                logs.Add(entry);
                        }
                    }
                }
                return logs;
            });
        }

        private static LogEntry ReadLogEntry(SQLiteDataReader reader)
        {
            if (!TryParseTimestamp(reader.GetString(0), out var timestamp))
                return null;
            return new LogEntry
            {
                Timestamp = timestamp,
                Voltage = reader.GetDouble(1),
                Current = reader.GetDouble(2),
                Power = reader.GetDouble(3),
                OutputEnabled = reader.GetInt64(4) == 1,
                CvMode = reader.GetInt64(5) == 1,
                CcMode = reader.GetInt64(6) == 1,
                UnregulatedMode = reader.GetInt64(7) == 1,
                OvpTrip = reader.GetInt64(8) == 1,
                OcpTrip = reader.GetInt64(9) == 1,
                SenseTrip = reader.GetInt64(10) == 1,
                FaultTrip = reader.GetInt64(11) == 1
            };
        }

        /// <summary>Time of the first and last sample in the database, or null when it is empty.</summary>
        public async Task<DateTime[]> GetDataExtentAsync()
        {
            return await Task.Run(() =>
            {
                using (var connection = Open())
                using (var command = new SQLiteCommand("SELECT MIN(T), MAX(T) FROM PowerLogs;", connection))
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read() || reader.IsDBNull(0))
                        return null;
                    return new[] { DateTime.FromOADate(reader.GetDouble(0)), DateTime.FromOADate(reader.GetDouble(1)) };
                }
            });
        }

        /// <summary>
        /// Samples for plotting. Up to <paramref name="maxRawPoints"/> rows are returned as they are;
        /// larger ranges are reduced to the minimum and maximum of each of <paramref name="buckets"/>
        /// time buckets (from the per-minute rollup for long ranges), so spikes stay visible.
        /// </summary>
        public async Task<HistorySeries> GetHistorySeriesAsync(DateTime startTime, DateTime endTime,
            int maxRawPoints = 20000, int buckets = 2000)
        {
            return await Task.Run(() =>
            {
                using (var connection = Open())
                {
                    var points = new SeriesBuilder();
                    double span = (endTime - startTime).TotalSeconds;

                    if (endTime - startTime > RollupThreshold)
                    {
                        double bucketSeconds = Math.Max(60.0, span / buckets);
                        using (var command = new SQLiteCommand(@"
                            SELECT CAST((M / 1440.0 - @T0) * 86400.0 / @Bucket AS INTEGER) AS B,
                                   MIN(TFirst), MAX(TLast), MIN(VMin), MAX(VMax), MIN(IMin), MAX(IMax), MIN(PMin), MAX(PMax), SUM(N)
                            FROM PowerLogsMinute
                            WHERE M BETWEEN @M0 AND @M1
                            GROUP BY B ORDER BY B;", connection))
                        {
                            AddRange(command, startTime, endTime);
                            AddMinuteRange(command, startTime, endTime);
                            command.Parameters.AddWithValue("@Bucket", bucketSeconds);
                            using (var reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                    points.AddEnvelope(reader, reader.GetInt64(9));
                            }
                        }
                        return points.Build(true, Math.Max(MaxGapSeconds, bucketSeconds * 2.5));
                    }

                    long rows;
                    using (var count = new SQLiteCommand("SELECT COUNT(*) FROM PowerLogs WHERE T BETWEEN @T0 AND @T1;", connection))
                    {
                        AddRange(count, startTime, endTime);
                        rows = Convert.ToInt64(count.ExecuteScalar(), Inv);
                    }

                    if (rows <= maxRawPoints)
                    {
                        using (var command = new SQLiteCommand(@"
                            SELECT T, Voltage, Current, Power FROM PowerLogs
                            WHERE T BETWEEN @T0 AND @T1 ORDER BY T;", connection))
                        {
                            AddRange(command, startTime, endTime);
                            using (var reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                    points.Add(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), 1);
                            }
                        }
                        return points.Build(false, MaxGapSeconds);
                    }

                    double rawBucketSeconds = Math.Max(1.0, span / buckets);
                    using (var command = new SQLiteCommand(@"
                        SELECT CAST((T - @T0) * 86400.0 / @Bucket AS INTEGER) AS B,
                               MIN(T), MAX(T), MIN(Voltage), MAX(Voltage), MIN(Current), MAX(Current), MIN(Power), MAX(Power), COUNT(*)
                        FROM PowerLogs
                        WHERE T BETWEEN @T0 AND @T1
                        GROUP BY B ORDER BY B;", connection))
                    {
                        AddRange(command, startTime, endTime);
                        command.Parameters.AddWithValue("@Bucket", rawBucketSeconds);
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                                points.AddEnvelope(reader, reader.GetInt64(9));
                        }
                    }
                    return points.Build(true, Math.Max(MaxGapSeconds, rawBucketSeconds * 2.5));
                }
            });
        }

        /// <summary>Collects strictly ascending plot points (SignalXY requirement).</summary>
        private sealed class SeriesBuilder
        {
            private readonly List<double> _xs = new List<double>();
            private readonly List<double> _v = new List<double>();
            private readonly List<double> _i = new List<double>();
            private readonly List<double> _p = new List<double>();
            private double _lastX = double.MinValue;
            private long _rows;

            public void Add(double x, double v, double i, double p, long rows)
            {
                _rows += rows;
                if (x <= _lastX)
                    return;
                _lastX = x;
                _xs.Add(x);
                _v.Add(v);
                _i.Add(i);
                _p.Add(p);
            }

            /// <summary>Bucket row: B, first T, last T, Vmin, Vmax, Imin, Imax, Pmin, Pmax. Minimum at the first sample, maximum at the last.</summary>
            public void AddEnvelope(SQLiteDataReader r, long rows)
            {
                Add(r.GetDouble(1), r.GetDouble(3), r.GetDouble(5), r.GetDouble(7), rows);
                Add(r.GetDouble(2), r.GetDouble(4), r.GetDouble(6), r.GetDouble(8), 0);
            }

            public HistorySeries Build(bool downsampled, double gapThresholdSeconds)
            {
                return _xs.Count == 0
                    ? HistorySeries.Empty
                    : new HistorySeries(_xs.ToArray(), _v.ToArray(), _i.ToArray(), _p.ToArray(), downsampled, _rows, gapThresholdSeconds);
            }
        }

        /// <summary>
        /// Exact statistics over all samples in the range. Long ranges combine whole minutes from the
        /// rollup table with the raw samples of the partial minutes at both ends.
        /// </summary>
        public async Task<HistoryStats> GetStatsAsync(DateTime startTime, DateTime endTime)
        {
            return await Task.Run(() =>
            {
                var total = new Aggregate();
                using (var connection = Open())
                {
                    double t0 = startTime.ToOADate();
                    double t1 = endTime.ToOADate();
                    long m0 = (long)Math.Ceiling(t0 * MinutesPerDay);
                    long m1 = (long)Math.Floor(t1 * MinutesPerDay);

                    if (endTime - startTime > TimeSpan.FromHours(2) && m1 > m0)
                    {
                        using (var command = new SQLiteCommand(@"
                            SELECT SUM(N), MIN(TFirst), MAX(TLast), MIN(VMin), MAX(VMax), SUM(VSum), MIN(IMin), MAX(IMax), SUM(ISum),
                                   MIN(PMin), MAX(PMax), SUM(PSum), SUM(E), SUM(Q), SUM(OnS)
                            FROM PowerLogsMinute WHERE M >= @A AND M < @B;", connection))
                        {
                            command.Parameters.AddWithValue("@A", m0);
                            command.Parameters.AddWithValue("@B", m1);
                            using (var reader = command.ExecuteReader())
                            {
                                if (reader.Read())
                                    total.Merge(reader);
                            }
                        }
                        MergeRaw(connection, total, t0, m0 / MinutesPerDay, false);
                        MergeRaw(connection, total, m1 / MinutesPerDay, t1, true);
                    }
                    else
                    {
                        MergeRaw(connection, total, t0, t1, true);
                    }

                    var stats = total.ToStats();
                    using (var command = new SQLiteCommand(@"
                        SELECT SUM(CASE WHEN Kind = 'Command' THEN 1 ELSE 0 END),
                               SUM(CASE WHEN Kind = 'CommError' OR Result IN ('Failed', 'Mismatch', 'Lost') THEN 1 ELSE 0 END),
                               SUM(CASE WHEN Kind = 'Alarm' AND Result = 'Raised' THEN 1 ELSE 0 END)
                        FROM CommandLog WHERE T BETWEEN @T0 AND @T1;", connection))
                    {
                        AddRange(command, startTime, endTime);
                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                stats.CommandCount = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), Inv);
                                stats.ErrorCount = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), Inv);
                                stats.AlarmCount = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2), Inv);
                            }
                        }
                    }
                    return stats;
                }
            });
        }

        /// <summary>Adds the raw samples with from &lt;= T &lt; to (or &lt;= to) to the aggregate.</summary>
        private static void MergeRaw(SQLiteConnection connection, Aggregate total, double from, double to, bool includeEnd)
        {
            if (to < from)
                return;
            using (var command = new SQLiteCommand($@"
                SELECT COUNT(*), MIN(T), MAX(T), MIN(Voltage), MAX(Voltage), SUM(Voltage), MIN(Current), MAX(Current), SUM(Current),
                       MIN(Power), MAX(Power), SUM(Power), SUM(Power * Dt), SUM(Current * Dt), SUM(OutputEnabled * Dt)
                FROM PowerLogs WHERE T >= @A AND T {(includeEnd ? "<=" : "<")} @B;", connection))
            {
                command.Parameters.AddWithValue("@A", from);
                command.Parameters.AddWithValue("@B", to);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                        total.Merge(reader);
                }
            }
        }

        /// <summary>Streams every sample in the range to a CSV file. Returns the number of rows written.</summary>
        public async Task<long> ExportLogsToCsvAsync(DateTime startTime, DateTime endTime, string path)
        {
            return await Task.Run(() =>
            {
                long rows = 0;
                using (var connection = Open())
                using (var command = new SQLiteCommand($@"
                    SELECT {LogColumns} FROM PowerLogs
                    WHERE T BETWEEN @T0 AND @T1 ORDER BY T;", connection))
                using (var writer = new StreamWriter(path))
                {
                    AddRange(command, startTime, endTime);
                    writer.WriteLine("Timestamp,Voltage,Current,Power,OutputEnabled,CvMode,CcMode,UnregulatedMode,OvpTrip,OcpTrip,SenseTrip,FaultTrip");
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            writer.Write(reader.GetString(0));
                            for (int c = 1; c <= 3; c++)
                                writer.Write("," + reader.GetDouble(c).ToString("R", Inv));
                            for (int c = 4; c <= 11; c++)
                                writer.Write("," + reader.GetInt64(c).ToString(Inv));
                            writer.WriteLine();
                            rows++;
                        }
                    }
                }
                return rows;
            });
        }

        /// <summary>Returns command, connection and error records in the range, oldest first.</summary>
        public async Task<List<ActivityRecord>> GetActivitiesAsync(DateTime startTime, DateTime endTime, int maxRows = MaxRowsPerQuery)
        {
            return await Task.Run(() =>
            {
                var records = new List<ActivityRecord>();
                using (var connection = Open())
                using (var command = new SQLiteCommand(@"
                    SELECT Id, Timestamp, Kind, Source, Command, Response, Result, Requested, Readback, Detail, DurationMs
                    FROM CommandLog
                    WHERE T BETWEEN @T0 AND @T1
                    ORDER BY T ASC, Id ASC
                    LIMIT @MaxRows;", connection))
                {
                    AddRange(command, startTime, endTime);
                    command.Parameters.AddWithValue("@MaxRows", maxRows);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (!TryParseTimestamp(reader.GetString(1), out var timestamp))
                                continue;
                            var record = new ActivityRecord(reader.GetString(2))
                            {
                                Id = reader.GetInt64(0),
                                Timestamp = timestamp,
                                Source = NullableString(reader, 3),
                                Command = NullableString(reader, 4),
                                Response = NullableString(reader, 5),
                                Requested = NullableDouble(reader, 7),
                                DurationMs = NullableDouble(reader, 10)
                            };
                            record.Complete(NullableString(reader, 6), NullableDouble(reader, 8), NullableString(reader, 9));
                            records.Add(record);
                        }
                    }
                }
                return records;
            });
        }

        #endregion

        #region Helpers

        public static string FormatTimestamp(DateTime timestamp)
        {
            return timestamp.ToString(TimestampFormat, Inv);
        }

        private static bool TryParseTimestamp(string text, out DateTime timestamp)
        {
            return DateTime.TryParseExact(text, TimestampFormat, Inv, DateTimeStyles.None, out timestamp);
        }

        private static void AddRange(SQLiteCommand command, DateTime startTime, DateTime endTime)
        {
            command.Parameters.AddWithValue("@T0", startTime.ToOADate());
            command.Parameters.AddWithValue("@T1", endTime.ToOADate());
        }

        private static void AddMinuteRange(SQLiteCommand command, DateTime startTime, DateTime endTime)
        {
            command.Parameters.AddWithValue("@M0", (long)Math.Floor(startTime.ToOADate() * MinutesPerDay));
            command.Parameters.AddWithValue("@M1", (long)Math.Floor(endTime.ToOADate() * MinutesPerDay));
        }

        private static string NullableString(SQLiteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? null : reader.GetValue(index).ToString();
        }

        private static double? NullableDouble(SQLiteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? (double?)null : Convert.ToDouble(reader.GetValue(index), Inv);
        }

        private static int Execute(SQLiteConnection connection, string sql, double? cutoff = null)
        {
            using (var command = new SQLiteCommand(sql, connection))
            {
                if (cutoff.HasValue)
                    command.Parameters.AddWithValue("@Cutoff", cutoff.Value);
                return command.ExecuteNonQuery();
            }
        }

        private static object Scalar(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection))
                return command.ExecuteScalar();
        }

        #endregion

        public void Dispose()
        {
            _flushTimer.Dispose();
        }
    }
}
