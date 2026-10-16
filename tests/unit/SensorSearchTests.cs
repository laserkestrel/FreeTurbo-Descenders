using DescendersBridge.Ant;

namespace DescendersBridge;

/// <summary>
/// A chosen sensor that is never found must not starve the trainer channel. The rules for when a sensor channel searches,
/// when it is closed and when it is tried again (<see cref="SensorSearch"/>, <see cref="SensorSearchRules"/>) on a simulated clock, with
/// a fake "radio" that plays the stick's part (ends a search by itself, or not). Run by `FreeTurbo-Bridge selftest`; no radio.
/// </summary>
public static class SensorSearchTests
{
    private static readonly DeviceKey Hr = new(AntDeviceTypes.HeartRate, 7000, 1), Cad = new(AntDeviceTypes.BikeCadence, 28600, 69);

    /// <summary>One sensor on the fake radio: present from <see cref="OnFrom"/> (seconds; infinity = never switched on).</summary>
    private sealed class Sensor
    {
        public readonly SensorSearch Search = new();
        public double OnFrom = double.PositiveInfinity, OffFrom = double.PositiveInfinity;
        public double LockSeconds = 2;            // a search finds a sensor that is on after this long
        public bool StickEndsSearch = true;       // the stick closes the channel when the search time runs out
        public double SearchStart = -1;
        public double SearchingSeconds, FirstData = -1;
        public int Opens, Closes;
        public bool On(double t) => t >= OnFrom && t < OffFrom;
    }

    /// <summary>The session's loop (AntSession.RunSensorsAsync) and the stick, in 0.25 s steps.</summary>
    private static void Run(Sensor[] sensors, double from, double to, Func<double, bool>? trainerPaired = null, bool trainerChosen = true)
    {
        double? unpairedSince = null;
        for (double t = from; t < to; t += 0.25)
        {
            // the stick
            foreach (var s in sensors)
            {
                var x = s.Search;
                if (x.State == SensorState.Tracking)
                {
                    if (s.On(t)) x.Data(t);
                    else { x.WentToSearch(t); s.SearchStart = t; }
                }
                else if (x.State == SensorState.Searching)
                {
                    s.SearchingSeconds += 0.25;
                    if (s.On(t) && t - s.SearchStart >= s.LockSeconds) { x.Data(t); if (s.FirstData < 0) s.FirstData = t; }
                    else if (s.StickEndsSearch && t - s.SearchStart >= SensorSearchRules.SearchWindow(x.LowPriority)) x.Closed(t, miss: true);
                }
            }
            // the session
            bool paired = trainerPaired?.Invoke(t) ?? true;
            if (paired) unpairedSince = null; else unpairedSince ??= t;
            bool holds = SensorSearchRules.TrainerHolds(trainerChosen, paired, t - (unpairedSince ?? t));
            foreach (var s in sensors)
            {
                bool another = sensors.Any(o => o != s && o.Search.State == SensorState.Searching);
                switch (s.Search.Next(t, another, holds))
                {
                    case SensorStep.Open: s.Search.Opened(t); s.SearchStart = t; s.Opens++; break;
                    case SensorStep.Close: s.Search.Closed(t, miss: !holds); s.Closes++; break;
                }
            }
        }
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Sensor search: the numbers");
        {
            check(SensorSearchRules.TimeoutUnits(10) == 4 && SensorSearchRules.TimeoutUnits(5) == 2 && SensorSearchRules.TimeoutUnits(0) == 1 && SensorSearchRules.TimeoutUnits(9999) == 254,
                "search time in the stick's 2.5 s units, never 0 (no search) or 255 (for ever)");
            check(SensorSearchRules.RetryGap(1, true, false) == 30 && SensorSearchRules.RetryGap(50, true, false) == 30, "low-priority search: a missing sensor is tried again every 30 s");
            check(SensorSearchRules.RetryGap(1, false, false) == 60 && SensorSearchRules.RetryGap(2, false, false) == 120 && SensorSearchRules.RetryGap(3, false, false) == 240
                && SensorSearchRules.RetryGap(4, false, false) == 300 && SensorSearchRules.RetryGap(1000, false, false) == 300,
                "stick without low-priority search: the pause doubles from 60 s to 5 min");
            check(SensorSearchRules.RetryGap(1, true, true) == 2 && SensorSearchRules.RetryGap(2, true, true) == 30 && SensorSearchRules.RetryGap(1, false, true) == 2,
                "a sensor that dropped out gets one more search after 2 s, then the normal pause");
            check(SensorSearchRules.SearchWindow(true) == 10 && SensorSearchRules.SearchWindow(false) == 5, "a search is 10 s (low priority) or 5 s");
            check(SensorSearchRules.TrainerHolds(true, false, 0) && SensorSearchRules.TrainerHolds(true, false, 29) && !SensorSearchRules.TrainerHolds(true, false, 30)
                && !SensorSearchRules.TrainerHolds(true, true, 0) && !SensorSearchRules.TrainerHolds(false, false, 0),
                "sensors wait while the trainer searches, for at most 30 s; no trainer chosen: no wait");
            check(SensorSearchRules.ReportMiss(1) && !SensorSearchRules.ReportMiss(2) && SensorSearchRules.ReportMiss(10) && !SensorSearchRules.ReportMiss(11) && !SensorSearchRules.ReportMiss(0),
                "the log gets the first miss and every tenth, not one line per search");
            string miss = SensorSearchRules.MissText(Hr, 1, true, false);
            check(miss.Contains("Heart rate #7000 not found") && miss.Contains("carries on without it") && miss.Contains("30 s"), $"the log line says it plainly ({miss})");
            check(SensorSearchRules.MissText(Hr, 2, true, true).Contains("gone quiet"), "a sensor heard earlier: gone quiet");
            check(SensorSearchRules.TrainerSearchPriority > 0, "the trainer's search priority is above the sensors' default 0");
        }

        Console.WriteLine("Sensor search: one channel's states");
        {
            var s = new SensorSearch();
            check(s.State == SensorState.Waiting && !s.NotFound && s.Next(0, false, false) == SensorStep.Open, "new: opened at once");
            check(s.Next(0, true, false) == SensorStep.None && s.Next(0, false, true) == SensorStep.None, "not while another sensor searches, not while the trainer is pairing");
            s.Opened(0);
            check(s.State == SensorState.Searching && s.Next(9, false, false) == SensorStep.None && s.Next(12.9, false, false) == SensorStep.None, "searching: left alone for its time");
            check(s.Next(13, false, false) == SensorStep.Close, "the stick did not end the search (10 s + 3 s): the bridge closes the channel");
            check(s.Next(4, false, true) == SensorStep.Close, "the trainer lost its link: the sensor search is closed at once");
            check(s.Closed(10, miss: true) && s.State == SensorState.Waiting && s.Misses == 1 && s.NotFound, "closed without data: one miss, not found");
            check(!s.Closed(10.1, miss: true) && s.Misses == 1, "the same close reported twice (the bridge, then the stick's event) counts once");
            check(!s.Data(10.2) && s.State == SensorState.Waiting, "a late message on a closed channel does not mark it as received");
            check(s.Next(39.9, false, false) == SensorStep.None && s.Next(40, false, false) == SensorStep.Open, "tried again 30 s after the close");
            s.Opened(40);
            check(s.Data(42) && s.State == SensorState.Tracking && s.Misses == 0 && !s.NotFound && s.HeardBefore, "switched on later: found, no longer 'not found'");
            check(!s.Data(42.25) && s.Next(60, false, true) == SensorStep.None, "receiving: nothing to do, also while the trainer re-pairs");
            check(s.Next(62.25, false, false) == SensorStep.Close, "receiving but silent for 20 s with no word from the stick: closed and searched for again");
            s.WentToSearch(50);
            check(s.State == SensorState.Searching && !s.NotFound, "dropped out: the stick searches again (not yet 'not found')");
            s.Closed(60, miss: true);
            check(s.NotFound && s.Next(61.9, false, false) == SensorStep.None && s.Next(62, false, false) == SensorStep.Open, "a drop-out: one more search 2 s later");
            s.Opened(62); s.Closed(72, miss: true);
            check(s.Misses == 2 && s.Next(101.9, false, false) == SensorStep.None && s.Next(102, false, false) == SensorStep.Open, "then every 30 s");
            s.Opened(102); s.Closed(104, miss: false);
            check(s.Misses == 2 && s.Next(104.9, false, false) == SensorStep.None && s.Next(105, false, false) == SensorStep.Open, "cut short for the trainer: no miss, reopened after 1 s");
            s.OpenFailed(105);
            check(s.State == SensorState.Waiting && s.Next(134, false, false) == SensorStep.None && s.Next(135, false, false) == SensorStep.Open, "the channel could not be opened: tried again after 30 s");
            var f = new SensorSearch { LowPriority = false };
            f.Opened(0);
            check(f.Next(7.9, false, false) == SensorStep.None && f.Next(8, false, false) == SensorStep.Close, "no low-priority search: closed by the bridge after 5 s + 3 s");
            f.Closed(5, miss: true);
            check(f.Next(64.9, false, false) == SensorStep.None && f.Next(65, false, false) == SensorStep.Open, "and tried again after 60 s");
        }

        Console.WriteLine("Sensor search: a strap left switched off, a cadence sensor that is on (the 2026-10-01 garage ride)");
        {
            var hr = new Sensor();
            var cad = new Sensor { OnFrom = 0 };
            var both = new[] { hr, cad };
            Run(both, 0, 300);
            check(cad.FirstData >= 0 && cad.FirstData <= 30, $"cadence is found within 30 s although heart rate is searched for first (at {cad.FirstData:0.#} s)");
            check(cad.Search.State == SensorState.Tracking && cad.Opens == 1, "cadence: one search, then received for the rest of the ride");
            check(hr.Search.NotFound && hr.Search.State != SensorState.Tracking && hr.FirstData < 0, "heart rate: not found");
            check(hr.Opens >= 7 && hr.Opens <= 8 && hr.SearchingSeconds <= 85, $"heart rate: searched 10 s in every 40 s, not for ever ({hr.Opens} searches, {hr.SearchingSeconds:0} s of 300 s)");
            check(hr.Closes == 0, "the stick ended every search itself");

            hr.OnFrom = 300;   // the strap goes on mid-ride
            Run(both, 300, 360);
            check(hr.FirstData >= 300 && hr.FirstData <= 345, $"the strap put on mid-ride is found within 45 s (after {hr.FirstData - 300:0.#} s)");
            check(hr.Search.State == SensorState.Tracking && !hr.Search.NotFound, "heart rate: received, no longer 'not found'");

            hr.OffFrom = 360;   // and off again
            Run(both, 360, 480);
            check(hr.Search.NotFound && hr.Search.Misses >= 2, "the strap taken off again: back to short searches at intervals");
        }

        Console.WriteLine("Sensor search: never two sensors searching at once; a stick that does not end the search");
        {
            var a = new Sensor { StickEndsSearch = false };
            var b = new Sensor { StickEndsSearch = false };
            var both = new[] { a, b };
            bool overlap = false;
            for (double t = 0; t < 240; t += 5)
            {
                Run(both, t, t + 5);
                overlap |= a.Search.State == SensorState.Searching && b.Search.State == SensorState.Searching;
            }
            check(!overlap && a.Opens >= 4 && b.Opens >= 4, $"two missing sensors take turns ({a.Opens} and {b.Opens} searches)");
            check(a.Closes == a.Search.Misses && a.Closes >= 4 && a.SearchingSeconds <= 240 * 0.4, $"the bridge closes each search itself after 13 s ({a.SearchingSeconds:0} s of 240 s searching)");
        }

        Console.WriteLine("Sensor search: the trainer has priority");
        {
            var hr = new Sensor();
            Run(new[] { hr }, 0, 20, t => t >= 3);
            check(hr.Opens == 1 && hr.SearchingSeconds <= 10, "no sensor search before the trainer has paired (3 s)");

            var late = new Sensor();
            Run(new[] { late }, 0, 45, t => false);
            check(late.Opens == 1 && late.SearchingSeconds > 0 && late.SearchingSeconds <= 10, "trainer not found for 30 s: the sensors are looked for anyway");

            var cut = new Sensor();
            Run(new[] { cut }, 0, 4);                 // searching since 0
            Run(new[] { cut }, 4, 9, t => false);     // the trainer link drops at 4 s
            check(cut.Search.State == SensorState.Waiting && cut.Closes == 1 && cut.Search.Misses == 0 && cut.SearchingSeconds <= 4.5,
                "the trainer lost its link: the sensor search stops while it re-pairs, and is not counted as a miss");
            Run(new[] { cut }, 9, 12);
            check(cut.Search.State == SensorState.Searching && cut.Opens == 2, "trainer back: the sensor search starts again");

            var none = new Sensor { OnFrom = 0 };
            Run(new[] { none }, 0, 5, t => false, trainerChosen: false);
            check(none.FirstData >= 0 && none.FirstData <= 3, "no trainer chosen: the sensor is searched for at once");
        }

        Console.WriteLine("Sensor search: the window's light");
        {
            check(Gui.HealthRules.Classify(new LinkInfo(Hr, 0, double.PositiveInfinity, NotFound: true), null) == Gui.Health.NotFound, "searched for and not there = not found");
            check(Gui.HealthRules.Classify(new LinkInfo(Cad, 400, 60, NotFound: true), null) == Gui.Health.NotFound, "heard earlier, searched for again and not there = not found");
            check(Gui.HealthRules.Classify(new LinkInfo(Hr, 0, double.PositiveInfinity), null) == Gui.Health.Searching, "first search still running = searching");
            check(Gui.HealthRules.Text(Gui.Health.NotFound) == "Not found" && Gui.HealthRules.Advice(Gui.Health.NotFound).Contains("carries on without it"),
                "the light says Not found only, its tooltip that the ride carries on without it");
            check(Gui.HealthRules.Text(Gui.Health.Good) == "Connected" && Enum.GetValues<Gui.Health>().All(h => !Gui.HealthRules.Text(h).Contains("trainer") && !Gui.HealthRules.Text(h).Contains("without")),
                "no narrative on a reading's light (\"Connected\", never \"from the trainer\" / \"riding without it\")");
        }
    }
}
