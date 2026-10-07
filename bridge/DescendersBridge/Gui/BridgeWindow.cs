using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DescendersBridge.Ant;
using DescendersCyclingPOC;

namespace DescendersBridge.Gui;

/// <summary>
/// The bridge's window: Ride (big live readings, graphs of the last 5 min (`graphs.historySeconds`),a health light per device, and one slim status
/// strip under them, Trainer and Game, <see cref="StripRules"/>; the window connects to the
/// saved devices by itself when it opens, <see cref="ConnectRules"/>, and there is no Start / Stop button), My devices (search and
/// choose), Rider and bike (the `rider` section of TrainerBridgeConfig.json), Resistance (the `training` section, trainer difficulty,
/// ERG limits, workout bias step; the workout pause time is in the file only,; a running ride picks a save up by itself), Workouts
/// (choose the training plan to follow, see its progress, start its sessions or any single workout, all through <see cref="Menu"/>)
/// and Logs (the build and the settings file's path on top, under them the text the console version prints, scrolling in its own
/// area, without the settings values; between them the workout library folder, and the logs folder, each with
/// Browse... and Save). Renamed two tabs (Training -> Resistance, Details -> Logs); their control Names are unchanged.
/// The tabs are shown in the order Ride, Workouts, My devices, Rider and bike, Resistance, Logs.
/// The Xbox pad drives the window too (LB / RB the tabs, the D-pad the arrow keys, A = Enter, B = "No" on a question), only
/// while the window is in front and no game is connected (<see cref="PadRules"/>); its questions are <see cref="QuestionBox"/>.
/// The look is a small design system: colours, fonts and spacing in <see cref="Theme"/>, the reusable controls (cards, buttons,
/// status lights, input fields, the tab strip) in <c>ModernControls.cs</c>, the reading tiles in <c>Controls.cs</c>. This file only places them.
/// Everything here is a layer over the console code: the ride is <see cref="Ride.RunAsync"/>, the search is <see cref="AntScanner"/>, the
/// settings are <see cref="TrainerBridgeConfig"/>, so the command line and the window always behave the same.
/// Every control has a Name, which UI Automation reports as its AutomationId (tests\bridge-gui\GuiSmoke.ps1).
/// </summary>
public sealed class BridgeWindow : Form
{
    /// <summary>A trainer still unheard this long after connecting (or silent this long after being heard) gets the "can't find your trainer" message.</summary>
    public const double TrainerMissingSeconds = 20, TrainerLostSeconds = 10;

    /// <summary>What the "My devices" tab says next to its search button before the first search.</summary>
    public const string SearchHint = "Switch on your trainer and sensors, put on your heart-rate strap and pedal during the search so they wake up. "
        + "Then choose a device for each job below and press \"Save\".";

    private readonly bool _demo;
    private readonly LogSink _log;
    private readonly RideController _ride;
    private readonly ILiveSource _live;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly ToolTip _tip = new() { AutoPopDelay = 15000 };
    private readonly Dictionary<Role, RateMeter> _rates = Enum.GetValues<Role>().ToDictionary(r => r, _ => new RateMeter());

    // banner
    private readonly Row _banner;
    private readonly Label _bannerText;
    private readonly ModernButton _bannerAction, _bannerClose;
    private Action? _bannerHandler;
    private FriendlyMessage? _bannerMessage;

    // Ride tab
    private readonly TabControl _tabs;
    private readonly TabPage _rideTab, _devicesTab, _riderTab, _trainingTab, _workoutsTab, _detailsTab;
    private readonly StatusIndicator _trainerState;   // the status strip under the readings, with _gameState
    private readonly ReadingTile _power, _cadence, _hr, _speed;
    private bool _failureShown, _trainerWarned, _closing;
    private bool _stickGone;   // as last seen, so its loss and its return are logged once each
    private string _gameTip = "";
    private double? _trainerLastGood;

    // My devices tab
    private readonly ModernButton _search, _saveDevices;
    private readonly ModernProgress _searchProgress;
    private readonly Label _searchStatus, _devicesStatus, _searchNote;
    private readonly ModernList _found;
    private const int SignalColumn = 4;   // of the found list: Device, Found by, Make, Number, Signal, Reading now
    private readonly Dictionary<Role, ComboBox> _choices = new();
    private readonly Dictionary<Role, StatusIndicator> _choiceStates = new();   // the light under each drop-down: saved / found / not found / unsaved
    private DevicePreferences _saved = new();   // the devices as last loaded / saved, for the idle health text
    private DeviceSearch? _searchRun;
    private bool _searching;   // from the Search press until its search is over: the connection is paused for it
    private IReadOnlyList<FoundDevice> _heard = Array.Empty<FoundDevice>();
    private bool _searched;   // a search has finished: a saved device it did not hear is "not found"

    // Rider tab
    private readonly NumericUpDown _riderKg, _bikeKg, _ftp, _wheel, _gear;
    private readonly ComboBox _bikeType;   // bike-type preset (CdA + Crr), sent to the game in the `rider` message
    private readonly Label _riderStatus;

    // The game on the Ride tab's status strip (the bridge is the hub)
    private readonly StatusIndicator _gameState;
    private double _launchedAt = double.NegativeInfinity;   // _clock seconds when Steam was last asked for the game
    private bool _closeConfirmed;

    // Resistance tab (called Training earlier)
    private readonly NumericUpDown _difficulty, _ergStart, _ergMin, _ergMax, _ergStep, _biasStep;
    private readonly Label _trainingStatus;

    // Workouts tab (workouts and training plans are chosen and started here)
    private readonly Label _activePlan, _nextSession, _workoutDetail;
    // left / right pickers instead of drop-downs, with an effort graph of the session / workout in the middle
    private readonly PickerRow _planChoice, _sessionChoice, _folderChoice, _workoutChoice;
    private readonly EffortGraphView _sessionGraph, _workoutGraph;
    private readonly EffortGraphCache _graphs = new();
    private readonly ModernProgress _planProgress;
    private readonly ModernButton _resetPlan;
    private readonly HintLine _planHint, _workoutHint;   // how to start, under the card that has the focus
    private readonly StatusIndicator _nowRiding;
    private readonly (Control Stop, Func<Task> Activate)[] _navStops;   // the up / down focus order, and what Enter starts on each
    // what the pickers hold, entry for entry (a null plan = "Choose a training plan")
    private List<PlanPickerEntry?> _plans = new();
    private PlanPickerEntry? _activePlanEntry;   // the plan whose sessions are shown
    private List<PlanSessionEntry> _sessions = new();
    private List<string> _folders = new();
    private List<WorkoutPickerEntry> _workouts = new();
    private DateTime _settingsStamp;   // the settings file's last write, as last shown (a ride records a finished plan session there)
    private string _menuNote = "";      // what the last press did, shown for a few seconds in place of what is running
    private double _menuNoteUntil;
    private Session? _demoSession;      // `gui --demo` has no ride loop: the tab drives a session of its own

    // Logs tab (called Details earlier; the workout library folder and the logs folder are set here)
    private readonly TextBox _logText, _libraryDir, _logsDir;
    private readonly Label _libraryStatus, _logsStatus;

    // The Xbox pad: polled on its own timer, acted on only while this window is in front and no game is connected
    private readonly System.Windows.Forms.Timer _padTimer = new() { Interval = 30 };
    private readonly PadInput _pad = new();
    private readonly PadReader _padReader = new();
    private readonly InputWatch _inputWatch;
    private bool _padLast;            // the pad was the last thing used (not the keys or the mouse): the hints name its buttons
    private bool _padListening, _padSeen;
    private bool _gameWasConnected;   // as last seen, so the window asks for the foreground once when the game goes
    private QuestionBox? _question;   // the question box that is open, if any: the pad answers it

    private static int S(int px) => Theme.S(px);

    /// <summary>The window is never made smaller than this.</summary>
    public static readonly Size SmallestSize = new(760, 600);

    /// <summary>
    /// The size the window opens at: 960 x 760 at 100 % screen scaling, grown with the scaling like everything inside it (so each
    /// tab still fits without a scroll bar), never bigger than the screen's free area (0 = not known), never under <see cref="SmallestSize"/>. Pure.
    /// </summary>
    public static Size StartSize(Size workArea, double scale)
    {
        int w = (int)Math.Round(960 * scale), h = (int)Math.Round(760 * scale);
        if (workArea.Width > 0) w = Math.Min(w, workArea.Width);
        if (workArea.Height > 0) h = Math.Min(h, workArea.Height);
        return new Size(Math.Max(w, SmallestSize.Width), Math.Max(h, SmallestSize.Height));
    }

    public BridgeWindow(bool demo, LogSink log)
    {
        _demo = demo;
        _log = log;
        _ride = new RideController(demo);
        _live = demo ? new DemoLiveSource() : new AntLiveSource();
        // the accent colour is a hand-edited setting, read before any control takes its colours; the file gains it with the default
        Theme.SetAccent(TrainerBridgeConfig.EnsureAccentColour(Console.WriteLine));

        Name = "BridgeWindow";
        // the build stamp in the title bar: which code this is, without opening the Logs tab
        Text = "Turbo Trainer Bridge" + (demo ? " (demo: made-up readings)" : "") + "   [build " + BuildStamp.Text + "]";
        if (AppIcon.Load() is { } icon) Icon = icon;
        Font = Theme.Body;
        MinimumSize = SmallestSize;
        Size = StartSize(Screen.FromPoint(Cursor.Position).WorkingArea.Size, Theme.Scale);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Page;
        ForeColor = Theme.Text;

        // ---- banner: a strip across the top, the accent colour for information, red for a problem ----
        _banner = new Row
        {
            Name = "MessagePanel", Dock = DockStyle.Top, Visible = false, Gap = S(10),
            Padding = new Padding(S(20), S(10), S(16), S(10)), BackColor = Theme.InfoWash, AccentBar = Theme.Accent,
        };
        _bannerText = new TextLabel { Name = "MessageText" };
        _bannerAction = new ModernButton { Name = "MessageAction", Kind = ButtonKind.Primary };
        _bannerClose = new ModernButton { Name = "MessageClose", Text = "Dismiss", Kind = ButtonKind.Ghost };
        _bannerAction.Click += (_, _) => { var h = _bannerHandler; HideMessage(); h?.Invoke(); };
        _bannerClose.Click += (_, _) => HideMessage();
        _banner.Controls.AddRange(new Control[] { _bannerText, _bannerAction, _bannerClose });
        _banner.Stretch = _bannerText;

        _tabs = new ModernTabs { Name = "Tabs", Dock = DockStyle.Fill };
        _rideTab = new TabPage("Ride") { Name = "RideTab", BackColor = BackColor };
        _devicesTab = new TabPage("My devices") { Name = "DevicesTab", BackColor = BackColor };
        _riderTab = new TabPage("Rider and bike") { Name = "RiderTab", BackColor = BackColor };
        // shown as "Resistance" and "Logs"; the Names (= AutomationIds) stay as they were
        _trainingTab = new TabPage("Resistance") { Name = "TrainingTab", BackColor = BackColor };
        _workoutsTab = new TabPage("Workouts") { Name = "WorkoutsTab", BackColor = BackColor };
        _detailsTab = new TabPage("Logs") { Name = "DetailsTab", BackColor = BackColor };
        // Workouts is the second tab
        _tabs.TabPages.AddRange(new[] { _rideTab, _workoutsTab, _devicesTab, _riderTab, _trainingTab, _detailsTab });
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedTab == _workoutsTab) ShowMenu(); };   // a ride may have moved the plan on, files may have been added
        Controls.Add(_tabs);
        Controls.Add(_banner);

        // ---- Ride tab: the dashboard. The four live readings and, under them, one slim status strip (Trainer and Game, each
        // a light plus a few words, `StripRules`); no "Riding / Not riding" line and no buttons (the window connects by itself) ----
        var ride = new StackPanel { Dock = DockStyle.Fill, Padding = new Padding(S(16)), Gap = S(12) };
        var grid = new UniformGrid { Name = "Readings", Columns = 2, Gap = S(12) };
        _power = new ReadingTile("Power", "Power", "watts", 200, 0, _tip);
        _cadence = new ReadingTile("Cadence", "Cadence", "rpm", 100, 0, _tip);
        _hr = new ReadingTile("HeartRate", "Heart rate", "bpm", 150, 0, _tip);
        _speed = new ReadingTile("Speed", "Speed", "km/h", 30, 1, _tip);
        grid.Controls.AddRange(new Control[] { _power, _cadence, _hr, _speed });
        ride.Controls.Add(grid);
        ride.Fill = grid;

        // the status strip: the trainer (with what the rider must act on: stick lost, stopping, failed) and the game (this
        // window is the hub), side by side in one font; a text too long for its half is cut with "..."
        var strip = new Columns { Name = "StatusStrip", CardStyle = true, Padding = new Padding(S(18), S(8), S(18), S(8)), Gap = S(24) };
        _trainerState = new StatusIndicator { Name = "TrainerState" };
        _gameState = new StatusIndicator { Name = "GameState" };
        strip.Controls.AddRange(new Control[] { _trainerState, _gameState });
        ride.Controls.Add(strip);
        _rideTab.Controls.Add(ride);

        // ---- My devices tab: find them, then say which one does which job ----
        var (devRoot, devBody) = NewPage(_devicesTab);
        // no separate heading and intro lines, so the whole page fits the window it opens at. The "Search for devices" button is
        // the card's heading; the how-to sits next to it until a search says something else there.
        var findCard = new Card();
        var searchRow = new Row { Gap = S(14) };
        _search = new ModernButton { Name = "SearchButton", Text = "Search for devices", Kind = ButtonKind.Primary };
        _search.Click += async (_, _) => await SearchAsync();
        _searchProgress = new ModernProgress { Name = "SearchProgress", AccessibleName = "Search progress" };
        _searchStatus = new TextLabel(SearchHint, Theme.Body, Theme.TextSecondary) { Name = "SearchStatus" };
        searchRow.Controls.AddRange(new Control[] { _search, _searchProgress, _searchStatus });
        searchRow.Stretch = _searchStatus;
        Ui.SetVisible(_searchProgress, false);
        findCard.Controls.Add(searchRow);
        // a quiet line while only one of ANT+ / Bluetooth is searched ("No ANT+ stick found: searching Bluetooth only");
        // it takes no room while there is nothing to say
        _searchNote = new TextLabel("", Theme.Small, Theme.Muted) { Name = "SearchNote" };
        Ui.SetVisible(_searchNote, false);
        findCard.Controls.Add(_searchNote);
        _found = new ModernList
        {
            Name = "FoundList",
            CellLight = (column, text) => column != SignalColumn ? null : text switch { "Strong" or "OK" => Theme.Good, "Weak" => Theme.Warning, "" => null, _ => Theme.Critical },
        };
        // a "Make" column (blank when the device did not say, or the brand is not in our list). A "Found by"
        // column, "ANT+" or "Bluetooth" (a trainer that does both is two rows); a Bluetooth device's Make is the name it advertises.
        // The five fixed columns are as narrow as their words allow ("Cadence sensor", "Bluetooth", a 17-letter name), so the last
        // one keeps room for a trainer's reading ("195 watts  84 rpm") at the smallest window, the list's scroll bar included
        _found.Columns.Add("Device", S(122));
        _found.Columns.Add("Found by", S(84));
        _found.Columns.Add("Make", S(140));
        _found.Columns.Add("Number", S(68));
        _found.Columns.Add("Signal", S(88));
        _found.Columns.Add("Reading now", S(160));
        findCard.Controls.Add(_found);
        findCard.Fill = _found;
        findCard.FillMin = S(130);
        devBody.Controls.Add(findCard);
        devBody.Fill = findCard;

        var useCard = new Card("Use these devices") { Dividers = true };
        AddChoice(useCard, Role.Trainer, "Trainer", "The smart trainer your bike sits on.");
        AddChoice(useCard, Role.HeartRate, "Heart rate", "A chest strap or a watch that broadcasts heart rate.");
        AddChoice(useCard, Role.Cadence, "Cadence", "Optional: a separate cadence sensor. Otherwise the trainer's own is used.");
        AddChoice(useCard, Role.Power, "Power", "Optional: pedals or a crank that measure power. Otherwise the trainer's own is used.");
        devBody.Controls.Add(useCard);
        _saveDevices = new ModernButton { Name = "SaveDevicesButton", Text = "Save", Kind = ButtonKind.Primary };
        _saveDevices.Click += (_, _) => SaveDevices();
        _devicesStatus = new TextLabel("", Theme.Body, Theme.TextSecondary) { Name = "DevicesStatus" };
        devRoot.Controls.Add(Footer(_devicesStatus, _saveDevices));

        // ---- Rider and bike tab ----
        var (riderRoot, riderBody) = NewPage(_riderTab);
        var riderCard = new Card("Rider", "These make the game and the trainer feel right for you: the heavier you and your bike are, the harder the climbs.") { Dividers = true };
        _riderKg = AddNumber(riderCard, "RiderWeight", "Your weight (kg)", RiderProfile.MinRiderKg, RiderProfile.MaxRiderKg, 1, "Your body weight in kilograms, with your cycling clothes on.");
        _ftp = AddNumber(riderCard, "Ftp", "FTP (watts)", RiderProfile.MinFtpW, RiderProfile.MaxFtpW, 0, "The power you could keep up for about an hour. Not sure? Leave it at 180.");
        riderBody.Controls.Add(riderCard);
        var bikeCard = new Card("Bike") { Dividers = true };
        _bikeKg = AddNumber(bikeCard, "BikeWeight", "Bike weight (kg)", RiderProfile.MinBikeKg, RiderProfile.MaxBikeKg, 1, "A road bike is usually 8 to 10 kg.");
        _bikeType = new ModernCombo { Name = "BikeType", Width = S(170), AccessibleName = "Bike type" };
        foreach (string b in BikePresets.Names) _bikeType.Items.Add(new BikeChoice(b));
        bikeCard.Controls.Add(new SettingRow("Bike type",
            "How fast your watts move you in the game: a road bike rolls fastest, a mountain bike has more air and tyre drag. Road bike = the feel you had before.", _bikeType));
        _wheel = AddNumber(bikeCard, "WheelDiameter", "Wheel size (metres)", RiderProfile.MinWheelM, RiderProfile.MaxWheelM, 2, "The wheel's diameter. A normal road bike wheel is about 0.70.");
        _gear = AddNumber(bikeCard, "GearRatio", "Gear ratio", 0, RiderProfile.MaxGearRatio, 2, "Only for trainers that simulate gears. Leave it at 1 if unsure (0 = don't send one).");
        riderBody.Controls.Add(bikeCard);
        var saveRider = new ModernButton { Name = "SaveRiderButton", Text = "Save", Kind = ButtonKind.Primary };
        saveRider.Click += (_, _) => SaveRider();
        var undo = new ModernButton { Name = "UndoRiderButton", Text = "Undo changes" };
        undo.Click += (_, _) => { LoadRider(TrainerBridgeConfig.Load().Rider); _riderStatus!.Text = "Changes undone."; };
        _riderStatus = new TextLabel("", Theme.Body, Theme.TextSecondary) { Name = "RiderStatus" };
        riderRoot.Controls.Add(Footer(_riderStatus, saveRider, undo));

        // ---- Resistance tab: the `training` section, applied to a running ride within a second ----
        var (trRoot, trBody) = NewPage(_trainingTab);
        var feelCard = new Card("Trainer feel", "How hard the trainer pushes back, and how ERG and workouts behave. Changes apply straight away, even in the middle of a ride.") { Dividers = true };
        _difficulty = AddNumber(feelCard, "TrainerDifficulty", "Trainer difficulty", Training.MinDifficulty, Training.MaxDifficulty, 2,
            "How much of each climb your legs feel. 0.35 (standard) turns a 13 % climb into 4.5 % on the trainer; 1 = the real slope, 0 = always flat. It never changes your speed in the game.");
        _difficulty.Increment = 0.05m;
        // the workout step sits here, the pause time is in the settings file only, so the tab needs no "Workouts" card and no scroll bar
        _biasStep = AddNumber(feelCard, "WorkoutBiasStepPct", "Workout step (%)", Training.MinBiasPct, Training.MaxBiasPct, 0, "In a workout, one press makes every target this much easier or harder. Standard: 5.");
        _biasStep.Increment = 1;
        trBody.Controls.Add(feelCard);
        var ergCard = new Card("ERG mode", "The trainer holds a target power for you, whatever your cadence.") { Dividers = true };
        _ergStart = AddNumber(ergCard, "ErgStartW", "ERG start target (watts)", Training.MinErgW, Training.MaxErgW, 0, "The target ERG starts at when you switch it on. Standard: 120.");
        _ergMin = AddNumber(ergCard, "ErgMinW", "ERG lowest target (watts)", Training.MinErgW, Training.MaxErgW, 0, "The target never goes below this. Standard: 30.");
        _ergMax = AddNumber(ergCard, "ErgMaxW", "ERG highest target (watts)", Training.MinErgW, Training.MaxErgW, 0, "The target never goes above this. Standard: 400.");
        _ergStep = AddNumber(ergCard, "ErgStepW", "ERG step (watts)", Training.MinStepW, Training.MaxStepW, 0, "How far one press of RB + D-pad Left / Right (or PageUp / PageDown) moves the target. Standard: 10.");
        _ergStep.Increment = 1;
        trBody.Controls.Add(ergCard);
        var saveTraining = new ModernButton { Name = "SaveTrainingButton", Text = "Save", Kind = ButtonKind.Primary };
        saveTraining.Click += (_, _) => SaveTraining();
        var trUndo = new ModernButton { Name = "UndoTrainingButton", Text = "Undo changes" };
        trUndo.Click += (_, _) => { LoadTraining(TrainerBridgeConfig.Load().Training); _trainingStatus!.Text = "Changes undone."; };
        var trStandard = new ModernButton { Name = "StandardTrainingButton", Text = "Back to standard" };
        trStandard.Click += (_, _) => { LoadTraining(new TrainingConfig()); _trainingStatus!.Text = "Standard values shown: press Save to use them."; };
        _trainingStatus = new TextLabel("", Theme.Body, Theme.TextSecondary) { Name = "TrainingStatus" };
        trRoot.Controls.Add(Footer(_trainingStatus, saveTraining, trUndo, trStandard));

        // ---- Workouts tab (the workout / training plan menu is here, not in the game). Left / right
        // pickers that never open a list (so the page needs no scroll bar however long the library is, and up / down / left / right / Enter
        // reach everything), with an effort graph of the session / workout in the middle. The training plan and the single workout sit
        // side by side (the library folder is on the Logs tab). No Start buttons (Enter on a row, or a click
        // on the chosen session / workout or on its graph, starts it; the line under the focused card says so), and the graphs take
        // only part of the height that is left: the rest makes the picker rows taller and the card roomier (`MenuCard`, `CardRules`) ----
        var (woRoot, woBody) = NewPage(_workoutsTab);
        // the cards are tighter than a settings card, so everything fits the minimum window
        static MenuCard NewMenuCard() => new() { Padding = new Padding(S(14), S(10), S(14), S(10)), Gap = S(6) };
        var planCard = NewMenuCard();
        _resetPlan = new ModernButton { Name = "ResetPlanButton", Text = "Reset plan" };
        _resetPlan.Click += async (_, _) => await ResetPlanAsync();
        var planTitle = new SectionHeader("Training plan") { Wrap = false };
        var planHead = new Row { Gap = S(10), Stretch = planTitle };
        planHead.Controls.AddRange(new Control[] { planTitle, _resetPlan });
        _planChoice = new PickerRow("Training plan") { Name = "PlanChoice" };
        _planChoice.Changed += (_, _) => PlanStepped();
        _planChoice.Settled += async (_, _) => await PlanChosenAsync();
        _activePlan = new TextLabel("", Theme.Small, Theme.TextSecondary) { Name = "ActivePlan" };
        _planProgress = new ModernProgress { Name = "PlanProgress", AccessibleName = "Training plan progress" };
        _sessionChoice = new PickerRow("Session") { Name = "SessionChoice" };
        _sessionChoice.Changed += (_, _) => ShowSessionGraph();
        _sessionChoice.Activated += async (_, _) => await StartSessionAsync();
        _sessionGraph = new EffortGraphView { Name = "SessionGraph", AccessibleName = "Effort graph of the session" };
        _sessionGraph.Activated += async (_, _) => { _sessionChoice.Select(); await StartSessionAsync(); };
        _nextSession = new TextLabel("", Theme.Small, Theme.Muted) { Name = "NextSession", Wrap = false };
        // the line under the graph keeps its room while it is empty, so the graph does not change height when it gets its words
        var sessionRow = new Row { Stretch = _nextSession, MinHeight = Theme.Small.Height };
        sessionRow.Controls.Add(_nextSession);
        planCard.Controls.AddRange(new Control[] { planHead, _planChoice, _activePlan, _planProgress, _sessionChoice, _sessionGraph, sessionRow });
        planCard.Fill = _sessionGraph;
        planCard.FillMin = EffortGraphView.MinHeight;
        _planHint = new HintLine { Name = "PlanHint" };

        var workoutCard = NewMenuCard();
        var workoutTitle = new SectionHeader("Single workout") { Wrap = false };
        var workoutHead = new Row { Stretch = workoutTitle, MinHeight = _resetPlan.Height };   // as tall as the plan card's heading, so the pickers line up
        workoutHead.Controls.Add(workoutTitle);
        _folderChoice = new PickerRow("Folder") { Name = "WorkoutFolderChoice" };
        _folderChoice.Changed += (_, _) => FolderStepped();
        _folderChoice.Settled += (_, _) => FillWorkouts(null);
        _workoutChoice = new PickerRow("Workout") { Name = "WorkoutChoice" };
        _workoutChoice.Changed += (_, _) => ShowWorkoutDetail();
        _workoutChoice.Activated += async (_, _) => await StartWorkoutAsync();
        _workoutGraph = new EffortGraphView { Name = "WorkoutGraph", AccessibleName = "Effort graph of the workout" };
        _workoutGraph.Activated += async (_, _) => { _workoutChoice.Select(); await StartWorkoutAsync(); };
        _workoutDetail = new TextLabel("", Theme.Small, Theme.Muted) { Name = "WorkoutDetail", Wrap = false };
        var workoutRow = new Row { Stretch = _workoutDetail, MinHeight = Theme.Small.Height };
        workoutRow.Controls.Add(_workoutDetail);
        // blank, as high as the plan card's progress line: both cards then share their height the same way, so their rows and graphs line up
        var progressRoom = new Panel { Height = Ui.Measure(_planProgress, 0).Height, Margin = Padding.Empty };
        workoutCard.Controls.AddRange(new Control[] { workoutHead, _folderChoice, progressRoom, _workoutChoice, _workoutGraph, workoutRow });
        workoutCard.Fill = _workoutGraph;
        workoutCard.FillMin = EffortGraphView.MinHeight;
        _workoutHint = new HintLine { Name = "WorkoutHint" };

        // each card with its hint line under it ("Enter or click to start", only under the card that has the focus)
        static StackPanel WithHint(Card card, HintLine hint)
        {
            var column = new StackPanel { Gap = S(4), Fill = card };
            column.Controls.AddRange(new Control[] { card, hint });
            return column;
        }
        var choose = new Columns { Gap = S(12) };
        choose.Controls.AddRange(new Control[] { WithHint(planCard, _planHint), WithHint(workoutCard, _workoutHint) });
        woBody.Controls.Add(choose);
        woBody.Fill = choose;
        // up / down walk these, top to bottom; Enter on any of them starts what its card offers (left / right only ever step).
        // "Reset plan" is the first stop while it has something to reset, so the arrows and the pad's D-pad reach it
        _navStops = new (Control, Func<Task>)[]
        {
            (_resetPlan, ResetPlanAsync),
            (_planChoice, StartSessionAsync), (_sessionChoice, StartSessionAsync),
            (_folderChoice, StartWorkoutAsync), (_workoutChoice, StartWorkoutAsync),
        };
        foreach (var (stop, _) in _navStops)
        {
            stop.Enter += (_, _) => ShowHints();
            stop.Leave += (_, _) => ShowHints();
        }

        // under the cards, always in view: what is running now, or what the last press did
        _nowRiding = new StatusIndicator { Name = "NowRiding" };
        woRoot.Controls.Add(_nowRiding);

        // ---- Logs tab: what never changes on top (the build, where the settings file is, the logs folder button), then
        // the two folders the rider chooses, the workout library (moved here from the Workouts tab) and the logs folder, each
        // a box with Browse... and Save, then the log, which scrolls in its own card ----
        var details = new StackPanel { Dock = DockStyle.Fill, Padding = new Padding(S(16)), Gap = S(12) };
        var aboutCard = new Card { Name = "AboutBox", Padding = new Padding(S(18), S(12), S(18), S(12)), Gap = S(8) };
        var aboutText = new StackPanel { Gap = S(4) };
        // the path is a link: a click shows the file in Explorer
        var configPath = new TextLabel("Settings file: " + TrainerBridgeConfig.Path, Theme.Small, Theme.Accent) { Name = "ConfigPath", Cursor = Cursors.Hand };
        configPath.Click += (_, _) => ShowSettingsFile();
        _tip.SetToolTip(configPath, "Show this file in its folder");
        aboutText.Controls.AddRange(new Control[] { new TextLabel("Build " + BuildStamp.Text, Theme.Small, Theme.Muted) { Name = "BuildText" }, configPath });
        var openLogs = new ModernButton { Name = "OpenLogsButton", Text = "Open the logs folder" };
        openLogs.Click += (_, _) => OpenLogsFolder();
        var aboutRow = new Row { Gap = S(14), Stretch = aboutText };
        aboutRow.Controls.AddRange(new Control[] { aboutText, openLogs });
        aboutCard.Controls.Add(aboutRow);
        details.Controls.Add(aboutCard);

        var foldersCard = new Card { Name = "FoldersBox", Padding = new Padding(S(18), S(12), S(18), S(12)), Gap = S(6) };
        (_libraryDir, _libraryStatus) = AddFolder(foldersCard, "Library", "Workout library", "Workout library folder", BrowseLibrary, SaveLibrary);
        (_logsDir, _logsStatus) = AddFolder(foldersCard, "Logs", "Logs folder", "Logs folder", BrowseLogs, SaveLogs);
        // the two headings are as wide as each other, so the boxes line up
        var folderHeads = foldersCard.Controls.OfType<Row>().Select(r => (TextLabel)r.Controls[0]).ToList();
        int headWidth = folderHeads.Max(h => h.Measure(0).Width);
        foreach (var h in folderHeads) h.MinWidth = headWidth;
        details.Controls.Add(foldersCard);

        var logCard = new Card("Technical log", "What the bridge is doing, line by line. Someone helping you may ask for this; it is also kept in the logs folder.");
        _logText = new TextBox
        {
            Name = "LogText", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None,
            Font = Theme.Mono, BackColor = Theme.Surface, ForeColor = Theme.TextSecondary,
        };
        Theme.DarkScrollBars(_logText);
        logCard.Controls.Add(_logText);
        logCard.Fill = _logText;
        logCard.FillMin = S(120);
        details.Controls.Add(logCard);
        details.Fill = logCard;
        _detailsTab.Controls.Add(details);

        _timer.Tick += (_, _) => Tick();
        _padTimer.Tick += (_, _) => PadPoll();
        _inputWatch = new InputWatch(() => SetPadLast(false));
        Load += (_, _) =>
        {
            CheckConfig(); CheckOtherBridge(); Connect(asked: false, say: _bannerMessage is null); _timer.Start();
            Application.AddMessageFilter(_inputWatch);
            _padTimer.Start();
        };
        Resize += (_, _) => { if (_bannerMessage is not null) FitBanner(); };
        FormClosing += OnClosing;
        FormClosed += (_, _) => { _padTimer.Stop(); Application.RemoveMessageFilter(_inputWatch); Ui.PadInUse = false; };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    // ================= page building =================

    /// <summary>A settings page: the cards scroll (Body) when the window is too short; whatever is added to Root after them stays in view.</summary>
    private static (StackPanel Root, StackPanel Body) NewPage(TabPage tab)
    {
        var root = new StackPanel { Dock = DockStyle.Fill, Padding = new Padding(S(16)), Gap = S(12) };
        var body = new StackPanel { Scrolls = true, Gap = S(12) };
        root.Controls.Add(body);
        root.Fill = body;
        tab.Controls.Add(root);
        return (root, body);
    }

    /// <summary>The row under a page's cards: its buttons, then what the last press did.</summary>
    private static Row Footer(Control status, params Control[] buttons)
    {
        var row = new Row { Gap = S(10), Stretch = status };
        row.Controls.AddRange(buttons);
        row.Controls.Add(status);
        return row;
    }

    /// <summary>One folder setting on the Logs tab: its heading, the path in a box, Browse... and Save on one line, what it means under it.
    /// The Names are <paramref name="key"/>Dir, Browse<paramref name="key"/>Button, Save<paramref name="key"/>Button and <paramref name="key"/>Status.</summary>
    private static (TextBox Dir, Label Status) AddFolder(Card card, string key, string title, string accessibleName, Action browse, Action save)
    {
        var dir = new TextBox { Name = key + "Dir", AccessibleName = accessibleName };
        var field = new FieldFrame(dir, S(420));
        var browseButton = new ModernButton { Name = "Browse" + key + "Button", Text = "Browse..." };
        browseButton.Click += (_, _) => browse();
        var saveButton = new ModernButton { Name = "Save" + key + "Button", Text = "Save" };
        saveButton.Click += (_, _) => save();
        var row = new Row { Gap = S(10), Stretch = field };
        row.Controls.AddRange(new Control[] { new SectionHeader(title) { Wrap = false }, field, browseButton, saveButton });
        card.Controls.Add(row);
        var status = new TextLabel("", Theme.Small, Theme.Muted) { Name = key + "Status" };
        card.Controls.Add(status);
        return (dir, status);
    }

    // ================= settings file =================

    private void CheckConfig()
    {
        string path = TrainerBridgeConfig.Path;
        if (!TrainerBridgeConfig.IsReadable(path, out string reason))
        {
            Console.WriteLine($"Bridge config: {path} is unreadable ({reason})");
            ShowMessage(FriendlyText.ConfigBroken, error: true, "Fix it for me", () =>
            {
                try
                {
                    string backup = TrainerBridgeConfig.ResetBroken(path);
                    Console.WriteLine("Bridge config: unreadable file kept as " + backup + "; started again with the defaults");
                    ReloadSettings();
                    ShowMessage(new FriendlyMessage("Fixed.", "Your settings are back to standard. The old file was kept as " + Path.GetFileName(backup) + "."), error: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR: " + ex);
                    ShowMessage(new FriendlyMessage("The settings file could not be repaired.", "Check that you can write to " + Path.GetDirectoryName(path) + ", then restart this program."), error: true);
                }
            });
            ReloadSettings();   // the defaults, for now
            return;
        }
        bool isNew = !File.Exists(path);
        ReloadSettings();
        if (isNew) ShowMessage(FriendlyText.Welcome, error: false, "Enter my details", () => _tabs.SelectedTab = _riderTab);
    }

    private void CheckOtherBridge()
    {
        if (_demo) return;
        try
        {
            using var me = Process.GetCurrentProcess();
            var others = Process.GetProcessesByName(me.ProcessName).Where(p => p.Id != me.Id).ToList();
            foreach (var p in others) p.Dispose();
            if (others.Count > 0 && _bannerMessage is null) ShowMessage(FriendlyText.AlreadyRunning, error: false);
        }
        catch { /* process list unavailable: the ride will say so if it matters */ }
    }

    private void ReloadSettings()
    {
        var cfg = TrainerBridgeConfig.Load();
        var (rider, prefs) = cfg;
        LoadRider(rider);
        LoadTraining(cfg.Training);
        // how far back the graphs go is a hand-edited setting; the file gains it with the default so it can be found there
        int history = TrainerBridgeConfig.EnsureGraphHistorySeconds(Console.WriteLine);
        foreach (var tile in new[] { _power, _cadence, _hr, _speed }) tile.Graph.History.WindowSeconds = history;
        // `gui --demo` shows a saved Bluetooth device too (in the window only; the file gets it on a Save, like any demo device)
        if (_demo && prefs.Get(Role.Power) is null) prefs.Set(Role.Power, DemoLiveSource.PowerMeter, name: DemoLiveSource.BluetoothPower.Name);
        _saved = prefs;
        FillChoices(prefs, keepSelection: false);
        _libraryDir.Text = TrainerBridgeConfig.LoadLibraryDir();
        _libraryStatus.Text = LibraryText.Status(_libraryDir.Text);
        _logsDir.Text = Ride.LogsDir;
        _logsStatus.Text = LogsFolder.Status(TrainerBridgeConfig.LoadLogsDir(), Ride.DefaultLogsDir);
        ShowMenu();
    }

    // ================= logs folder (on the Logs tab, next to the workout library) =================

    private void BrowseLogs()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose the folder for the bridge log and the ride recordings",
            UseDescriptionForTitle = true, ShowNewFolderButton = true,
        };
        if (Directory.Exists(_logsDir.Text.Trim())) dlg.SelectedPath = _logsDir.Text.Trim();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _logsDir.Text = dlg.SelectedPath;
        SaveLogs();
    }

    private void SaveLogs()
    {
        string dir = _logsDir.Text.Trim();
        if (LogsFolder.Refuse(dir) is { } why) { _logsStatus.Text = why; return; }
        string store = LogsFolder.ToStore(dir, Ride.DefaultLogsDir);
        try { if (store.Length > 0) Directory.CreateDirectory(store); }
        catch (Exception ex)
        {
            Console.WriteLine("Logs folder " + store + " could not be made: " + ex.Message);
            _logsStatus.Text = "Not saved: that folder could not be made. Choose another one with \"Browse...\".";
            return;
        }
        try
        {
            TrainerBridgeConfig.SaveLogsDir(store);
            Console.WriteLine("Logs folder saved: " + (store.Length > 0 ? store : "(standard) " + Ride.DefaultLogsDir));
            _logsDir.Text = Ride.LogsDir;
            _logsStatus.Text = "Saved. " + LogsFolder.SavedNote;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR saving the logs folder: " + ex.Message);
            _logsStatus.Text = "Not saved.";
            CheckConfig();
        }
    }

    // ================= workout library (on the Logs tab), training plan and workouts =================

    private void BrowseLibrary()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose your workout library folder (it holds the Workouts and TrainingPlans folders)",
            UseDescriptionForTitle = true, ShowNewFolderButton = true,
        };
        if (Directory.Exists(_libraryDir.Text.Trim())) dlg.SelectedPath = _libraryDir.Text.Trim();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _libraryDir.Text = dlg.SelectedPath;
        SaveLibrary();
    }

    private void SaveLibrary()
    {
        string dir = _libraryDir.Text.Trim();
        if (LibraryText.Refuse(dir) is { } why) { _libraryStatus.Text = why; return; }
        try
        {
            TrainerBridgeConfig.SaveLibraryDir(dir);
            Console.WriteLine("Workout library saved: " + (dir.Length > 0 ? dir : "(none)"));
            _libraryStatus.Text = "Saved. " + LibraryText.Status(dir);
            ShowMenu();
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR saving the workout library: " + ex.Message);
            _libraryStatus.Text = "Not saved.";
            CheckConfig();
        }
    }

    private static DateTime SettingsStamp()
    {
        try { return File.GetLastWriteTimeUtc(TrainerBridgeConfig.Path); }
        catch { return default; }
    }

    /// <summary>The entry a picker has in the middle, out of the list it was filled from.</summary>
    private static T? Chosen<T>(List<T> list, PickerRow picker) where T : class? => picker.Index >= 0 && picker.Index < list.Count ? list[picker.Index] : null;

    /// <summary>
    /// Fill the whole tab from the settings file (plan progress: read only here, the presses below write it) and the library folder, read
    /// again every time: the plans with their progress, the active plan's sessions with the next one chosen, the workout folders and
    /// workouts (the rider's folder and workout stay chosen).
    /// </summary>
    private void ShowMenu()
    {
        _settingsStamp = SettingsStamp();
        string dir = TrainerBridgeConfig.LoadLibraryDir();
        string? lib = Menu.UsableLibrary(dir, out string problem);
        var p = ProgressFile.Load(TrainerBridgeConfig.Path);
        float ftp = (float)_ftp.Value;

        string planNote = "";
        var plans = lib is null || p.Problem is not null ? new List<PlanPickerEntry>() : PlanPicker.Plans(lib, p.Store, out planNote);
        var active = _activePlanEntry = plans.Find(e => e.Active);
        _plans = new List<PlanPickerEntry?>();
        if (active is null && plans.Count > 0) _plans.Add(null);
        _plans.AddRange(plans);
        _planChoice.Fill(_plans.Select(e => e is null ? Menu.ChoosePlanEntry(plans.Count) : Menu.PlanEntry(e)).ToList(), active is null ? 0 : _plans.IndexOf(active));

        _sessions = new List<PlanSessionEntry>();
        if (active is not null)
        {
            p.Store.Plans.TryGetValue(active.Plan.Key, out PlanProgressEntry? entry);
            _sessions = PlanPicker.Sessions(lib!, active.Plan, entry, PlanBook.IncludeOptional, ftp);
        }
        _sessionChoice.Fill(_sessions.Select(Menu.SessionEntry).ToList(), Math.Max(0, _sessions.FindIndex(e => e.IsNext)));
        ShowSessionGraph();
        _resetPlan.Enabled = active is not null && active.Counts.Done + active.Counts.OptionalDone > 0;

        // the plan picker itself says which plan is followed and how far it is: the line under it is for when there is none, or a problem
        if (p.Problem is not null) (_activePlan.Text, _nextSession.Text) = ("Your training plan progress could not be read from the settings file (" + p.Problem + ").", "");
        else if (lib is null) (_activePlan.Text, _nextSession.Text) = (dir.Length == 0 ? PlanSummary.NoLibrary : problem + ".", "");
        else if (plans.Count == 0 && string.IsNullOrEmpty(p.Store.ActivePlan)) (_activePlan.Text, _nextSession.Text) = (planNote + ".", "");
        else
        {
            var (activeText, next) = PlanSummary.Describe(lib, p.Store);
            (_activePlan.Text, _nextSession.Text) = (active is null ? activeText : "", next);
        }
        _planProgress.Value = p.Problem is null ? PlanSummary.Percent(dir, p.Store) : 0;

        string keepFolder = Chosen(_folders, _folderChoice) ?? "", keepFile = Chosen(_workouts, _workoutChoice)?.File ?? "";
        _folders = lib is null ? new List<string>() : WorkoutPicker.Folders(Menu.WorkoutsDir(lib));
        string workoutsDir = lib is null ? "" : Menu.WorkoutsDir(lib);
        _folderChoice.Fill(_folders.Select(s => Menu.FolderEntry(s, WorkoutPicker.Count(workoutsDir, s))).ToList(),
            Math.Max(0, _folders.FindIndex(s => string.Equals(s, keepFolder, StringComparison.OrdinalIgnoreCase))));
        FillWorkouts(keepFile);
    }

    /// <summary>The workouts of the chosen folder; <paramref name="keepFile"/> (relative to the Workouts folder) stays chosen when it is still there.</summary>
    private void FillWorkouts(string? keepFile)
    {
        _workouts = new List<WorkoutPickerEntry>();
        string dir = TrainerBridgeConfig.LoadLibraryDir(), message;
        if (Menu.UsableLibrary(dir, out string problem) is not { } lib) message = dir.Length == 0 ? "Choose your workout library folder on the Logs tab first." : problem + ".";
        else if (Chosen(_folders, _folderChoice) is not { } sub) message = "No \"Workouts\" folder in the workout library yet: put your workout files (.zwo or .xml) in one.";
        else _workouts = WorkoutPicker.Workouts(Menu.WorkoutsDir(lib), sub, (float)_ftp.Value, out message);
        _workoutChoice.Fill(_workouts.Select(Menu.WorkoutEntry).ToList(),
            Math.Max(0, _workouts.FindIndex(e => keepFile is not null && string.Equals(e.File, keepFile, StringComparison.OrdinalIgnoreCase))));
        ShowWorkoutDetail(_workouts.Count == 0 ? message : null);
    }

    /// <summary>The effort graph and the words of the workout in the middle of the picker; with none, <paramref name="empty"/> says why in the graph's place.</summary>
    private void ShowWorkoutDetail(string? empty = null)
    {
        var e = Chosen(_workouts, _workoutChoice);
        _workoutDetail.Text = e is { Selectable: true } ? Menu.WorkoutDetail(e) : "";
        _workoutGraph.Graph = e is null ? EffortGraph.Note(empty ?? "") : e.Selectable ? _graphs.Get(e.Path, (float)_ftp.Value) : EffortGraph.Note(Menu.WorkoutDetail(e));
        ShowHints();
    }

    /// <summary>The hint under each card: how to start, while the focus is on one of the card's rows and it has something to start.</summary>
    private void ShowHints()
    {
        bool onPlan = ReferenceEquals(ActiveControl, _planChoice) || ReferenceEquals(ActiveControl, _sessionChoice);
        bool onWorkout = ReferenceEquals(ActiveControl, _folderChoice) || ReferenceEquals(ActiveControl, _workoutChoice);
        _planHint.Text = NavRules.Hint(onPlan, Chosen(_sessions, _sessionChoice) is { Selectable: true }, _padLast);
        _workoutHint.Text = NavRules.Hint(onWorkout, Chosen(_workouts, _workoutChoice) is { Selectable: true }, _padLast);
    }

    /// <summary>The effort graph of the session in the middle of the picker; one whose workout file is missing says so in the graph's place.</summary>
    private void ShowSessionGraph()
    {
        var s = Chosen(_sessions, _sessionChoice);
        _sessionGraph.Graph = s is null ? EffortGraph.Empty : s.Selectable ? _graphs.Get(s.Path, (float)_ftp.Value) : EffortGraph.Note(Menu.SessionProblem(s));
        ShowHints();
    }

    /// <summary>The plan picker moved: the sessions shown are another plan's until the rider stops stepping and that plan is followed
    /// (<see cref="PlanChosenAsync"/>), so they go for now.</summary>
    private void PlanStepped()
    {
        _sessions = new List<PlanSessionEntry>();
        _sessionChoice.Fill(Array.Empty<PickerEntry>(), -1);
        ShowSessionGraph();
        _resetPlan.Enabled = false;
    }

    /// <summary>The folder picker moved: its workouts are listed when the rider stops stepping (a folder is read from the disk, too slow
    /// for every step of a held key).</summary>
    private void FolderStepped()
    {
        _workouts = new List<WorkoutPickerEntry>();
        _workoutChoice.Fill(Array.Empty<PickerEntry>(), -1);
        ShowWorkoutDetail();
    }

    /// <summary>
    /// One command from an input source on the Workouts tab (the arrow keys and Enter today, a gamepad or a remote later): up /
    /// down move the focus between the picker rows (up from the first goes back to the tab strip; there are no Start buttons),
    /// previous / next step the focused picker and never start anything, activate starts what the focused card offers. False = not for
    /// this tab (the focus is somewhere else).
    /// </summary>
    internal bool WorkoutsCommand(NavCommand command)
    {
        if (_tabs.SelectedTab != _workoutsTab) return false;
        int at = Array.FindIndex(_navStops, s => ReferenceEquals(s.Stop, ActiveControl));
        bool onTabs = ReferenceEquals(ActiveControl, _tabs);
        switch (command)
        {
            case NavCommand.Up or NavCommand.Down:
                if (at < 0 && !(onTabs && command == NavCommand.Down)) return false;
                int to = NavRules.Move(at, command == NavCommand.Down ? 1 : -1, _navStops.Select(s => s.Stop is PickerRow p ? p.Count > 0 : s.Stop.Enabled).ToArray());
                if (to != at) _navStops[to].Stop.Select();
                else if (command == NavCommand.Up) _tabs.Select();
                ShowHints();
                return true;
            case NavCommand.Previous or NavCommand.Next:
                if (at < 0) return false;
                if (_navStops[at].Stop is PickerRow picker) picker.StepBy(command == NavCommand.Next ? 1 : -1);
                return true;
            default:
                if (at < 0) return false;
                _ = _navStops[at].Activate();
                return true;
        }
    }

    /// <summary>The keys of the Workouts tab: the arrows and Enter, when the focus is on a picker row.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        NavCommand? command = keyData switch
        {
            Keys.Up => NavCommand.Up,
            Keys.Down => NavCommand.Down,
            Keys.Left => NavCommand.Previous,
            Keys.Right => NavCommand.Next,
            Keys.Enter => NavCommand.Activate,
            _ => null,
        };
        if (command is { } c && WorkoutsCommand(c)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ================= the Xbox pad (the pure rules are in PadNav.cs) =================

    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, SwRestore = 9;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);

    /// <summary>Tells the window when the keys or the mouse are used (so the hints stop naming the pad's buttons). Changes nothing.</summary>
    private sealed class InputWatch : IMessageFilter
    {
        private readonly Action _used;
        public InputWatch(Action used) { _used = used; }

        public bool PreFilterMessage(ref Message m)
        {
            // key down, system key down, a mouse button down (inside the window, or on its frame)
            if (m.Msg is 0x0100 or 0x0104 or 0x0201 or 0x0204 or 0x0207 or 0x00A1) _used();
            return false;
        }
    }

    private void SetPadLast(bool pad)
    {
        if (_padLast == pad) return;
        _padLast = Ui.PadInUse = pad;
        ShowHints();
        ActiveControl?.Invalidate();   // the focus mark of a button or a tab comes and goes with it
        _tabs.Invalidate();
    }

    /// <summary>Every 30 ms: read the pad when it is listened to (<see cref="PadRules.Listen"/>: this window or its question box in
    /// front, and no game connected; XInput gives every press to every program) and act on what was pressed.</summary>
    private void PadPoll()
    {
        if (_closing) return;
        double t = _clock.Elapsed.TotalSeconds;
        bool foreground = Form.ActiveForm is { } front && (ReferenceEquals(front, this) || ReferenceEquals(front, _question));
        bool listen = PadRules.Listen(foreground, !_demo && GameConnected());
        if (listen != _padListening)
        {
            _padListening = listen;
            Console.WriteLine(listen ? "Xbox pad: listening (this window is in front and no game is connected)"
                : "Xbox pad: not listening (" + (foreground ? "a game is connected" : "this window is not in front") + ")");
        }
        PadStep(listen ? _pad.Read(t) : PadButtons.None, listen, t);
    }

    /// <summary>One reading of the pad: the buttons held down, whether the pad is listened to, the clock in seconds. The timer calls
    /// this; `selftest` feeds it made-up readings.</summary>
    internal void PadStep(PadButtons held, bool listen, double t)
    {
        foreach (var action in _padReader.Step(held, listen, t)) PadDo(action);
    }

    /// <summary>`selftest` only: the pad's own timer stops, so nothing but the test's readings reaches <see cref="PadStep"/>.</summary>
    internal void StopPadTimer() => _padTimer.Stop();

    /// <summary>The question box that is open now, for `selftest`.</summary>
    internal QuestionBox? OpenQuestion => _question;

    private void PadDo(PadAction action)
    {
        if (!_padSeen) { _padSeen = true; Console.WriteLine("Xbox pad: first press (" + action + ")"); }
        SetPadLast(true);
        if (_question is { } question) { question.Pad(action); return; }
        switch (action)
        {
            case PadAction.TabPrevious or PadAction.TabNext:
                _tabs.SelectedIndex = PadRules.NextTab(_tabs.SelectedIndex, _tabs.TabCount, action == PadAction.TabNext ? 1 : -1);
                _tabs.Select();   // the D-pad then walks down into the page
                break;
            case PadAction.Back:
                break;   // only a question box has something to go back from
            default:
                if (PadRules.Command(action) is { } command && !WorkoutsCommand(command)) PadKey(command);
                break;
        }
    }

    /// <summary>Outside the Workouts tab's rows the D-pad and A are the arrow keys and Enter, given to the focused control the way the
    /// keyboard's are (a focused button is pressed by A).</summary>
    private void PadKey(NavCommand command)
    {
        Control target = this;
        while (target is IContainerControl { ActiveControl: { } inner }) target = inner;
        if (ReferenceEquals(target, this) || !target.IsHandleCreated) return;
        if (command == NavCommand.Activate && target is IButtonControl button) { button.PerformClick(); return; }
        var key = (IntPtr)(int)(command switch
        {
            NavCommand.Up => Keys.Up,
            NavCommand.Down => Keys.Down,
            NavCommand.Previous => Keys.Left,
            NavCommand.Next => Keys.Right,
            _ => Keys.Enter,
        });
        var down = Message.Create(target.Handle, WmKeyDown, key, IntPtr.Zero);
        if (!target.PreProcessMessage(ref down)) SendMessage(target.Handle, WmKeyDown, key, IntPtr.Zero);
        if (target.IsHandleCreated) SendMessage(target.Handle, WmKeyUp, key, IntPtr.Zero);
    }

    /// <summary>The game went from connected to not connected: back to this window without a mouse. Windows may refuse and only flash
    /// the taskbar button; that is accepted.</summary>
    private void ComeToFront()
    {
        Console.WriteLine("The game is no longer connected: asking for the foreground (the Xbox pad works in this window again)");
        if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, SwRestore);
        Activate();
    }

    /// <summary>A yes / no question the pad can answer too (D-pad left / right, A, B = No). True = yes.</summary>
    internal bool Ask(string question)
    {
        using var box = new QuestionBox(question);
        _question = box;
        try { return box.ShowDialog(this) == DialogResult.Yes; }
        finally { _question = null; }
    }

    /// <summary>What the last press did, shown under the cards for a few seconds.</summary>
    private void MenuNote(string text)
    {
        if (text.Length == 0) return;
        _menuNote = text;
        _menuNoteUntil = _clock.Elapsed.TotalSeconds + 8;
        _nowRiding.Set(text, Theme.Accent);
        Console.WriteLine("Workouts tab: " + text);
    }

    /// <summary>Run <paramref name="action"/> on the running ride's session, on the ride loop's own thread (<see cref="RideLoop.Post"/>);
    /// null = no ride to run it on.</summary>
    private async Task<MenuResult?> OnRideAsync(Func<Session, DateTime, MenuResult> action)
    {
        if (_ride.Current != RideController.State.Running) return null;
        if (_demo)
            return action(_demoSession ??= new Session(new SessionConfig(), () => (double)_ftp.Value, Console.WriteLine, PlanBook.Load(TrainerBridgeConfig.Path, Console.WriteLine)), DateTime.UtcNow);
        if (RideLoop.Current is not { } loop) return null;
        var posted = loop.Post(action);
        return await Task.WhenAny(posted, Task.Delay(TimeSpan.FromSeconds(3))) == posted ? posted.Result : new MenuResult(false, "The ride did not answer: try again.");
    }

    /// <summary>Start something on the ride; without a ride, say why there is none. Once the workout is armed, open Descenders
    /// when no game is connected (<see cref="GameLauncher.LaunchOnStart"/>); its clock waits for the game to report riding.</summary>
    private async Task StartOnRideAsync(Func<Session, DateTime, MenuResult> start)
    {
        var r = await OnRideAsync(start) ?? new MenuResult(false, _ride.Current == RideController.State.Running
            ? "The ride is still connecting: try again in a moment."
            : ConnectRules.CannotStartText(_ride.Current, ConnectRules.AnyDevice(_saved), _searching));
        // the demo has no game: it always counts as not connected here, so the press is logged as "would launch"
        double sinceHeard = _demo ? double.PositiveInfinity : Ride.CurrentMod?.SecondsSinceHeard ?? double.PositiveInfinity;
        bool opening = GameLauncher.LaunchOnStart(r.Ok, sinceHeard, _clock.Elapsed.TotalSeconds - _launchedAt) && LaunchDescenders();
        MenuNote(opening ? r.Text.TrimEnd() + " " + GameLauncher.OpeningText : r.Text);
        if (r.Ok) ShowMenu();
    }

    /// <summary>A change to the plan progress. It has one writer at a time: the ride's own copy while a ride runs, else the settings file.</summary>
    private async Task ChangePlanAsync(Func<PlanBook, MenuResult> change)
    {
        var r = await OnRideAsync((s, _) => change(s.Plans)) ?? change(PlanBook.Load(TrainerBridgeConfig.Path, Console.WriteLine));
        MenuNote(r.Text);
        ShowMenu();
    }

    /// <summary>The plan the rider stopped on in the picker becomes the plan being followed (its sessions and progress show below).</summary>
    private async Task PlanChosenAsync()
    {
        if (Chosen(_plans, _planChoice) is not { Active: false } e) { ShowMenu(); return; }   // back on the plan already followed (or on none): its sessions again
        await ChangePlanAsync(book => Menu.FollowPlan(book, e.Plan.Key, e.Plan.Name, Console.WriteLine));
    }

    private async Task ResetPlanAsync()
    {
        if (_planChoice.Pending || _activePlanEntry is not { } e) return;
        if (!_demo && !Ask("Forget every finished session of \"" + e.Plan.Name + "\" and start the plan again from its first session?")) return;
        await ChangePlanAsync(book => Menu.ResetPlan(book, e.Plan.Key, e.Plan.Name, Console.WriteLine));
    }

    private async Task StartSessionAsync()
    {
        if (_activePlanEntry is not { } plan || Chosen(_sessions, _sessionChoice) is not { } s) return;
        if (!s.Selectable) { MenuNote("That session's workout file is missing from the workout library: " + s.Error); return; }
        string lib = TrainerBridgeConfig.LoadLibraryDir();
        await StartOnRideAsync((session, now) => Menu.StartPlanSession(session, lib, plan.Plan.Key, s.Ref, now));
    }

    private async Task StartWorkoutAsync()
    {
        if (Chosen(_workouts, _workoutChoice) is not { Selectable: true } e) return;
        await StartOnRideAsync((session, now) => Menu.StartWorkout(session, e.Path, now));
    }

    /// <summary>The line under the cards (what is running, or the last press; nothing while not riding), and a refill when the
    /// settings file changed under the tab (the ride recorded a finished plan session). Called on every timer tick.</summary>
    private void TickMenu(bool riding, double t)
    {
        if (!riding) _demoSession = null;
        if (t < _menuNoteUntil) _nowRiding.Set(_menuNote, Theme.Accent);
        else if (!riding) _nowRiding.Set("", null);   // an empty line takes no room; a Start press without a ride still says what to do
        else
        {
            string line = _demo ? _demoSession?.Describe() ?? Session.NoWorkoutText : RideLoop.Current?.SessionLine ?? "Connecting...";
            _nowRiding.Set(line, Theme.Good, hollow: line == Session.NoWorkoutText);
        }
        bool stepping = _planChoice.Pending || _folderChoice.Pending;   // the rider is still moving a picker: its own refill comes when it settles
        if (_tabs.SelectedTab == _workoutsTab && !stepping && SettingsStamp() != _settingsStamp) ShowMenu();
    }

    private void LoadRider(RiderConfig rider)
    {
        Set(_riderKg, rider.RiderWeightKg);
        Set(_bikeKg, rider.BikeWeightKg);
        Set(_ftp, rider.FtpW);
        Set(_wheel, rider.WheelDiameterM);
        Set(_gear, rider.GearRatio);
        string type = BikePresets.Normalize(rider.BikeType) ?? BikePresets.Default;
        _bikeType.SelectedIndex = Math.Max(0, Array.IndexOf(BikePresets.Names, type));
        static void Set(NumericUpDown n, double v) => n.Value = Math.Clamp((decimal)v, n.Minimum, n.Maximum);
    }

    /// <summary>One entry of the bike-type list: shows "Road bike", stores "road".</summary>
    private sealed record BikeChoice(string Name)
    {
        public override string ToString() => BikePresets.Label(Name);
    }

    private void SaveRider()
    {
        var r = new RiderConfig   // reading NumericUpDown.Value also commits text typed but not yet entered
        {
            RiderWeightKg = (double)_riderKg.Value, BikeWeightKg = (double)_bikeKg.Value, FtpW = (double)_ftp.Value,
            WheelDiameterM = (double)_wheel.Value, GearRatio = (double)_gear.Value,
            BikeType = _bikeType.SelectedItem is BikeChoice b ? b.Name : BikePresets.Default,
        };
        try
        {
            var saved = TrainerBridgeConfig.SaveRider(r);
            Console.WriteLine("Rider and bike saved: " + RiderProfile.Describe(saved));
            _riderStatus.Text = "Saved.";
            _ = ReconnectAsync();   // the new values are used at once
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR saving the rider: " + ex.Message);
            _riderStatus.Text = "Not saved.";
            CheckConfig();
        }
    }

    // ================= training =================

    private void LoadTraining(TrainingConfig t)
    {
        Set(_difficulty, t.TrainerDifficulty);
        Set(_ergStart, t.ErgDefaultW);
        Set(_ergMin, t.ErgMinW);
        Set(_ergMax, t.ErgMaxW);
        Set(_ergStep, t.ErgStepW);
        Set(_biasStep, t.WorkoutBiasStepPct);
        static void Set(NumericUpDown n, double v) => n.Value = Math.Clamp((decimal)v, n.Minimum, n.Maximum);
    }

    private void SaveTraining()
    {
        var t = new TrainingConfig   // reading NumericUpDown.Value also commits text typed but not yet entered
        {
            TrainerDifficulty = (double)_difficulty.Value, ErgDefaultW = (double)_ergStart.Value, ErgMinW = (double)_ergMin.Value, ErgMaxW = (double)_ergMax.Value,
            ErgStepW = (double)_ergStep.Value, WorkoutBiasStepPct = (double)_biasStep.Value,
        };
        try
        {
            t = Training.KeepHidden(t, TrainerBridgeConfig.LoadTraining(TrainerBridgeConfig.Path, _ => { }));   // the pause time has no field here
            var saved = TrainerBridgeConfig.SaveTraining(t);
            Console.WriteLine("Training settings saved: " + Training.Describe(saved));
            LoadTraining(saved);   // shows what was stored (a start target outside the range is moved into it)
            _trainingStatus.Text = saved != t ? "Saved, with some values moved into range." : "Saved.";
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR saving the training settings: " + ex.Message);
            _trainingStatus.Text = "Not saved.";
            CheckConfig();
        }
    }

    // ================= devices =================

    private sealed record Choice(DeviceKey? Key, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>One job on the "My devices" tab: what it is, the drop-down to choose the device, and a light saying where that choice stands.</summary>
    private void AddChoice(StackPanel card, Role role, string title, string help)
    {
        var box = new ModernCombo { Name = role + "Choice", Width = S(300), AccessibleName = title };
        var state = new StatusIndicator { Name = role + "ChoiceState", Font = Theme.Small };
        _choices[role] = box;
        _choiceStates[role] = state;
        box.SelectedIndexChanged += (_, _) => ShowChoiceStates();
        var editor = new StackPanel { Width = S(300), Gap = S(4) };
        editor.Controls.AddRange(new Control[] { box, state });
        card.Controls.Add(new SettingRow(title, help, editor));
    }

    /// <summary>The light under each drop-down: not set, saved, saved and found by the search, saved but not found, or changed and not saved yet.</summary>
    private void ShowChoiceStates()
    {
        foreach (var (role, box) in _choices)
        {
            if (!_choiceStates.TryGetValue(role, out var light)) continue;
            var chosen = (box.SelectedItem as Choice)?.Key;
            var state = DeviceChoiceRules.Classify(chosen, _saved.Get(role), chosen is { } c && _heard.Any(h => h.Is(c)), _searched);
            bool fallback = role is Role.Power or Role.Cadence;
            light.Set(DeviceChoiceRules.Text(state, fallback), Theme.HealthColor(DeviceChoiceRules.Light(state)), hollow: state == ChoiceState.NotSet);
            string advice = DeviceChoiceRules.Advice(state, fallback);   // the whole sentence, as on the Ride tab's lights
            if (_tip.GetToolTip(light) != advice) _tip.SetToolTip(light, advice);
        }
    }

    /// <summary>A device's name in a drop-down: with its brand when the search just heard it or the settings file remembers it.
    /// Brand and number only: no kind, no reading. And how it is reached, "(ANT+)" or "(Bluetooth)";
    /// a Bluetooth device by the name the search just heard, else the one the settings file remembers.</summary>
    private string Describe(DeviceKey k, DevicePreferences prefs) =>
        DeviceLabels.Choice(k, HeardMake(k) ?? prefs.ManufacturerOf(k), HeardName(k) ?? prefs.NameOf(k));

    private ushort? HeardMake(DeviceKey k) => _heard.FirstOrDefault(h => h.Is(k))?.ManufacturerId;

    private string? HeardName(DeviceKey k) => _heard.FirstOrDefault(h => h.Is(k))?.Name;

    /// <summary>Fill each role's list: none / trainer's own, the saved device (even if not heard), every heard device that can do the job,
    /// and the devices used before (a job set to none keeps its old device on offer).</summary>
    private void FillChoices(DevicePreferences prefs, bool keepSelection, bool suggest = true)
    {
        foreach (var (role, box) in _choices)
        {
            var current = keepSelection && box.SelectedItem is Choice c ? c.Key : prefs.Get(role);   // after a search, keep what the rider picked
            var items = new List<Choice> { new(null, role is Role.Power or Role.Cadence ? "Use the trainer's own" : "None") };
            // what each heard device would be saved as for this job (a Bluetooth trainer is offered as the trainer only);
            // the ANT+ devices first, so a trainer heard both ways is suggested by ANT+ (`DeviceChoiceRules.HeardFor`)
            var offered = DeviceChoiceRules.Offered(role, prefs.Get(role), DeviceChoiceRules.HeardFor(role, _heard), prefs.KnownFor(role));
            foreach (var (key, origin) in offered)
                items.Add(new(key, Describe(key, prefs) + DeviceChoiceRules.Suffix(origin)));

            // nothing chosen yet: suggest the trainer / strap heard most (what `run --auto` would pick); never one that is only remembered
            if (suggest && current is null && role is Role.Trainer or Role.HeartRate)
                current = offered.Where(o => o.Origin == ChoiceOrigin.Heard && o.Key.DeviceType == (role == Role.Trainer ? AntDeviceTypes.FitnessEquipment : AntDeviceTypes.HeartRate))
                    .Select(o => (DeviceKey?)o.Key).FirstOrDefault();

            box.BeginUpdate();
            box.Items.Clear();
            foreach (var i in items) box.Items.Add(i);
            box.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Key == current));
            box.EndUpdate();
        }
        ShowChoiceStates();
    }

    private void SaveDevices()
    {
        try
        {
            var prefs = TrainerBridgeConfig.Load().Devices;
            foreach (var (role, box) in _choices)
            {
                if (box.SelectedItem is Choice { Key: { } k }) prefs.Set(role, k, HeardMake(k), HeardName(k) ?? _saved.NameOf(k));
                else prefs.Clear(role);
            }
            prefs.Save();
            _saved = prefs;
            FillChoices(prefs, keepSelection: true, suggest: false);   // a device just set to none is now listed as "used before"
            Console.WriteLine("Devices saved:");
            Setup.Show(prefs);
            _devicesStatus.Text = "Saved.";
            if (_bannerMessage == FriendlyText.NoTrainerChosen && prefs.Get(Role.Trainer) is not null) HideMessage();
            _ = ReconnectAsync();   // the devices just saved are connected at once
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR saving the devices: " + ex.Message);
            _devicesStatus.Text = "Not saved.";
            CheckConfig();
        }
    }

    /// <summary>The USB stick can only do one thing at a time, and a Bluetooth device that is connected is not found by a search, so
    /// the search pauses the connection by itself, searches, and connects again afterwards (with whatever is saved by then).
    /// One search looks on ANT+ and Bluetooth together, on whatever this PC has (<see cref="DeviceSearch"/>).</summary>
    private async Task SearchAsync()
    {
        if (_searching || _closing) return;
        _searching = true;
        if (_bannerMessage is { } old && (old.Title == FriendlyText.TrainerNotHeardInSearch.Title || old == FriendlyText.NoStick || old == FriendlyText.NoStickBluetoothHere || old == FriendlyText.NoRadio)) HideMessage();
        var run = _searchRun = new DeviceSearch(_demo);
        _search.Enabled = false;
        _searchProgress.Value = 0;
        Ui.SetVisible(_searchProgress, true);
        ShowSearchNote(null);
        try
        {
            if (_ride.Current is RideController.State.Running or RideController.State.Stopping)
            {
                _searchStatus.Text = "Pausing the connection to your devices...";
                Console.WriteLine("Device search: pausing the connection first (the USB stick can only do one thing at a time, and a connected Bluetooth device is not found)");
                await _ride.StopAsync();
            }
            _searchStatus.Text = "Searching... pedal to wake the trainer up.";
            Console.WriteLine("Searching for devices for " + DeviceSearch.Seconds + " s (ANT+ and Bluetooth, whichever this PC has)");
            await run.RunAsync();
        }
        finally { _searching = false; }

        Ui.SetVisible(_searchProgress, false);
        _search.Enabled = !_closing;
        _search.Text = "Search again";
        Connect(asked: true, say: false);   // before the search's own messages: a new connection clears the old ones
        if (run.Failure is { } failure)
        {
            ShowSearchNote(null);
            _searchStatus.Text = "The search did not work.";
            ShowMessage(failure, error: true, failure == FriendlyText.NoKey ? null : "Try again", failure == FriendlyText.NoKey ? null : async () => await SearchAsync());
            return;
        }
        ShowSearchNote(run.Note);   // stays under the button: what was found is only what that half of the search could hear
        _heard = run.Devices;
        _searched = true;
        // devices saved earlier gain their brand at the first search that hears them, without being chosen again
        var makes = _heard.Where(h => h.ManufacturerId.HasValue).Select(h => (h.Key, h.ManufacturerId!.Value)).ToList();
        if (makes.Count > 0) TrainerBridgeConfig.LearnManufacturers(makes, Console.WriteLine);
        foreach (var (key, make) in makes) _saved.Learn(key, make);
        ShowFound(_heard);
        FillChoices(_saved, keepSelection: true);
        int trainers = _heard.Count(d => d.KeyFor(Role.Trainer) is not null);
        _searchStatus.Text = _heard.Count == 0 ? "Nothing found." : $"Found {_heard.Count} device(s). Choose below, then press Save.";
        if (trainers == 0)
            ShowMessage(FriendlyText.TrainerNotHeard(run.SearchedAnt, run.SearchedBluetooth), error: true, "Search again", async () => await SearchAsync());
    }

    /// <summary>The quiet line under the search button; null = nothing to say, and the line takes no room.</summary>
    private void ShowSearchNote(string? note)
    {
        string text = note is null ? "" : note + ".";
        if (_searchNote.Text == text) return;
        _searchNote.Text = text;
        Ui.SetVisible(_searchNote, text.Length > 0);
    }

    private void ShowFound(IReadOnlyList<FoundDevice> devices)
    {
        _found.BeginUpdate();
        while (_found.Items.Count > devices.Count) _found.Items.RemoveAt(_found.Items.Count - 1);
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            string[] cells = { d.Kind, DeviceLabels.Transport(d.Key), DeviceLabels.FoundMake(d.Key, d.ManufacturerId, d.Name), DeviceLabels.FoundNumber(d.Key), d.Signal, d.Reading };
            if (i >= _found.Items.Count) _found.Items.Add(new ListViewItem(cells) { Name = "Found" + i });
            else for (int c = 0; c < cells.Length; c++) if (_found.Items[i].SubItems[c].Text != cells[c]) _found.Items[i].SubItems[c].Text = cells[c];
        }
        _found.EndUpdate();
    }

    // ================= ride =================

    /// <summary>
    /// Connect if <see cref="ConnectRules.Decide"/> says so (a device saved, the ANT+ key there, no search running, not
    /// connected already). <paramref name="say"/>: with nothing to connect to, show the banner that says what to do.
    /// </summary>
    private void Connect(bool asked, bool say)
    {
        if (_closing) return;
        // the ANT+ key only matters when a saved device is ANT+
        var step = ConnectRules.Decide(_ride.Current, ConnectRules.AnyDevice(_saved), ConnectRules.KeyOk(_saved.NeedsAnt, BridgeConfig.KeySource != "none"), _searching, asked, _demo);
        if (step == ConnectStep.Connect) StartRide();
        else if (step == ConnectStep.NoDevice && say) ShowMessage(FriendlyText.NoTrainerChosen, error: true, "Go to My devices", () => _tabs.SelectedTab = _devicesTab);
        else if (step == ConnectStep.NoKey && say) ShowMessage(FriendlyText.NoKey, error: true);
    }

    /// <summary>The saved devices or the rider changed, or the rider pressed "Try again": drop the connection (if any) and connect again
    /// with what is saved now. While a search runs nothing happens here: the search connects again when it is done.</summary>
    private async Task ReconnectAsync()
    {
        if (_closing || _searching) return;
        await _ride.StopAsync();
        Connect(asked: true, say: false);
    }

    private void StartRide()
    {
        if (_bannerMessage is { } m && (m.Title == FriendlyText.TrainerNotFound.Title || m == FriendlyText.NoStick || m == FriendlyText.NoStickBluetoothHere || m == FriendlyText.AlreadyRunning || m.Title == "Something went wrong."
            || m.Advice == FriendlyText.NoBluetooth("").Advice)) HideMessage();
        foreach (var r in _rates.Values) r.Reset();
        _failureShown = _trainerWarned = false;
        _trainerLastGood = null;
        var problem = _ride.Start();
        if (problem is not null)
        {
            ShowMessage(problem, error: true, problem == FriendlyText.NoTrainerChosen ? "Go to My devices" : null,
                problem == FriendlyText.NoTrainerChosen ? () => _tabs.SelectedTab = _devicesTab : null);
            return;
        }
        // not over another message (Welcome on a first run, say): the window connects by itself now, nobody pressed anything
        if (_ride.Devices?.Get(Role.Trainer) is null && _bannerMessage is null)
            ShowMessage(new FriendlyMessage("No trainer chosen:", "showing your sensors only, the trainer's resistance will not change."), error: false);
    }

    private void Tick()
    {
        _logText.SuspendLayout();
        string more = _log.Drain();
        if (more.Length > 0)
        {
            if (_logText.TextLength > 200_000) _logText.Text = _logText.Text[^100_000..];
            _logText.AppendText(more);
        }
        _logText.ResumeLayout();

        if (_searchRun is { Running: true } s)
        {
            _searchProgress.Value = (int)(s.Progress * 100);
            ShowFound(s.Devices);
            ShowSearchNote(s.Note);
        }

        var state = _ride.Current;
        bool riding = state == RideController.State.Running;
        // the stick lost under a running ride, and back: logged once each (the other changes of the ride state: RideController.Current)
        bool stickGone = riding && Ride.StickGone;
        if (riding && StripRules.LogLine(StripRules.Phase(state, _stickGone), StripRules.Phase(state, stickGone)) is { } stickLine) Console.WriteLine(stickLine);
        _stickGone = stickGone;
        if (state == RideController.State.Failed && !_failureShown && _ride.Failure is { } f)
        {
            _failureShown = true;
            // no stick on a PC with Bluetooth is a note, not the red error, and it does not take the place of what
            // a search has just said (the search connects again when it is done, and fails again while the saved devices are ANT+ ones)
            bool error = FriendlyText.IsError(f);
            if (error || _bannerMessage?.Title != FriendlyText.TrainerNotHeardInSearch.Title)
                ShowMessage(f, error, f == FriendlyText.NoKey ? null : "Try again", f == FriendlyText.NoKey ? null : () => Connect(asked: true, say: true));
        }

        double t = _clock.Elapsed.TotalSeconds;
        TickMenu(riding, t);
        var live = riding ? _live.Read() : LiveReading.None;
        Update(_power, Role.Power, ReadingText.Whole(live.PowerW), live.PowerW);
        Update(_cadence, Role.Cadence, ReadingText.Whole(live.CadenceRpm), live.CadenceRpm);
        Update(_hr, Role.HeartRate, ReadingText.Whole(live.HeartRateBpm), live.HeartRateBpm);
        var trainerHealth = Update(_speed, Role.Trainer, ReadingText.SpeedKmh(live.SpeedMps), live.SpeedMps * 3.6);
        if (riding) WatchTrainer(trainerHealth, t);

        // the status strip: the trainer, with what the rider must act on (stick lost, stopping, failed), and the game.
        // A Bluetooth trainer has its own words (connecting, connected, lost and reconnecting) and none about the stick
        var savedTrainer = _saved.Get(Role.Trainer);
        var trainerLine = StripRules.Trainer(state, stickGone, _searching, trainerHealth, savedTrainer?.DeviceNumber,
            savedTrainer is { } st ? _saved.ManufacturerOf(st) : null,
            savedTrainer is { IsBluetooth: true } bt ? DeviceLabels.BluetoothShort(bt, _saved.NameOf(bt)) : null,
            wasConnected: _trainerLastGood is not null, noStickOnly: _ride.Failure == FriendlyText.NoStickBluetoothHere);
        _trainerState.Set(trainerLine.Text, Theme.HealthColor(trainerLine.Light), trainerLine.Hollow);

        var mod = _demo ? null : Ride.CurrentMod;
        bool listening = _demo ? riding : mod is not null;
        var hello = _demo ? (riding ? new GameHello("descenders", "demo", new[] { "gradient", "speed", "hud" }) : null) : mod?.Hello;
        double sinceHeard = _demo ? 0 : mod?.SecondsSinceHeard ?? double.PositiveInfinity;
        bool gameConnected = listening && GamePresence.Connected(sinceHeard);
        _gameState.Set(GamePresence.Short(listening, hello, sinceHeard), gameConnected ? Theme.Good : Theme.Muted, hollow: !gameConnected);
        string gameTip = GamePresence.Describe(listening, hello, sinceHeard);   // the whole sentence, with what to do
        if (gameTip != _gameTip) _tip.SetToolTip(_gameState, _gameTip = gameTip);
        // the game has gone (quit, or 5 s without a packet): come back to the front, where the pad works again
        if (!_demo && PadRules.BringToFront(_gameWasConnected, gameConnected)) ComeToFront();
        _gameWasConnected = gameConnected;

        Health Update(ReadingTile tile, Role role, string text, double? value)
        {
            tile.Graph.Add(t, riding ? value : null);
            var link = live.LinkFor(role);
            double? rate = link is { } l ? _rates[role].Add(t, l.Messages) : null;
            if (link is null) _rates[role].Reset();
            var h = HealthRules.Classify(link, rate);
            if (!riding)
            {
                bool chosen = _saved.Get(role) is not null || (role is Role.Power or Role.Cadence && _saved.Get(Role.Trainer) is not null);
                tile.ShowIdle(text, chosen ? ConnectRules.IdleText(state, _searching) : HealthRules.Text(Health.NotChosen));
                return Health.NotChosen;
            }
            // "Tacx 44226" once the device has said who made it (in this ride, or remembered); the ride saves it to the file
            if (link is { ManufacturerId: { } make } heardLink) _saved.Learn(heardLink.Key, make);
            tile.Show(text, h, link is not { } k ? ""
                : k.Key.IsBluetooth ? DeviceLabels.BluetoothShort(k.Key, _saved.NameOf(k.Key))
                : DeviceLabels.Short(k.Key.DeviceNumber, k.ManufacturerId ?? _saved.ManufacturerOf(k.Key)),
                bluetooth: link is { Key.IsBluetooth: true });
            return h;
        }
    }

    /// <summary>The trainer not heard for <see cref="TrainerMissingSeconds"/> after connecting, or gone for <see cref="TrainerLostSeconds"/>: say so once, with a retry.</summary>
    private void WatchTrainer(Health h, double t)
    {
        if (_ride.Devices?.Get(Role.Trainer) is not { } trainer) return;
        if (h == Health.Good || h == Health.Weak)
        {
            _trainerLastGood = t;
            if (_trainerWarned && _bannerMessage?.Title == FriendlyText.TrainerNotFound.Title) HideMessage();
            _trainerWarned = false;
            return;
        }
        double since = _trainerLastGood is { } g ? t - g : (DateTime.UtcNow - _ride.StartedUtc).TotalSeconds;
        double limit = _trainerLastGood is null ? TrainerMissingSeconds : TrainerLostSeconds;
        if (!_trainerWarned && since > limit)
        {
            _trainerWarned = true;
            Console.WriteLine($"Trainer not heard for {since:0} s: telling the rider");
            // the stick advice for an ANT+ trainer, "one app at a time" for a Bluetooth one
            ShowMessage(FriendlyText.TrainerMissing(trainer.IsBluetooth), error: true, "Try again", async () => await ReconnectAsync());
        }
    }

    // ================= banner, logs, closing =================

    private void ShowMessage(FriendlyMessage m, bool error, string? actionText = null, Action? action = null)
    {
        _bannerMessage = m;
        _bannerText.Text = m.Text;
        _banner.BackColor = error ? Theme.ErrorWash : Theme.InfoWash;
        _banner.AccentBar = error ? Theme.Critical : Theme.Accent;
        _bannerAction.Text = actionText ?? "";
        Ui.SetVisible(_bannerAction, actionText is not null);
        _bannerHandler = action;
        FitBanner();
        _banner.Visible = true;
    }

    /// <summary>The message bar is as tall as its text needs at the window's width (it wraps), so the tabs below get the rest.</summary>
    private void FitBanner()
    {
        int wanted = _banner.Measure(ClientSize.Width).Height;
        if (_banner.Height != wanted) _banner.Height = wanted;
        _banner.PerformLayout();
    }

    private void HideMessage()
    {
        _banner.Visible = false;
        _bannerMessage = null;
        _bannerHandler = null;
    }

    private void OpenLogsFolder()
    {
        try
        {
            string dir = Ride.UsableLogsDir(Console.WriteLine);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Console.WriteLine("Could not open the logs folder: " + ex.Message); }
    }

    /// <summary>The settings file's link on the Logs tab: its folder in Explorer, with the file selected.</summary>
    private void ShowSettingsFile()
    {
        if (_demo) { Console.WriteLine("Demo: would show " + TrainerBridgeConfig.Path + " in Explorer"); return; }
        try
        {
            string path = TrainerBridgeConfig.Path;
            string args = File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + Path.GetDirectoryName(path) + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Console.WriteLine("Could not show the settings file: " + ex.Message); }
    }

    // ================= games =================

    /// <summary>Open Descenders through Steam (from a Start press on the Workouts tab, so this window is connected already and
    /// the game finds this bridge and starts no copy of its own). False = Steam could not be asked; the banner says so.</summary>
    private bool LaunchDescenders()
    {
        string uri = GameLauncher.SteamUri(GameLauncher.DescendersSteamAppId);
        _launchedAt = _clock.Elapsed.TotalSeconds;
        if (_demo) { Console.WriteLine("Demo: would launch Descenders through Steam (" + uri + ")"); return true; }
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            Console.WriteLine("Launching Descenders through Steam (" + uri + ")");
            return true;
        }
        catch (Exception ex)
        {
            _launchedAt = double.NegativeInfinity;   // nothing was opened: the next Start press tries again
            Console.WriteLine("Could not launch Descenders: " + ex.Message);
            ShowMessage(FriendlyText.SteamMissing, error: true);
            return false;
        }
    }

    private bool GameConnected() => Ride.CurrentMod is { } m && GamePresence.Connected(m.SecondsSinceHeard);

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        // a game still connected: ask first; either way the trainer is released below before the bridge exits
        if (!_closing && !_closeConfirmed && !_demo && GameConnected())
        {
            if (!Ask(GamePresence.CloseQuestion))
            {
                e.Cancel = true;
                return;
            }
            _closeConfirmed = true;
            Console.WriteLine("Window closed with a game connected (the rider said yes)");
        }
        if (_closing || _ride.Current is not (RideController.State.Running or RideController.State.Stopping) || _demo) { _closing = true; _timer.Stop(); return; }
        e.Cancel = true;   // release the trainer first, then close
        _closing = true;
        _search.Enabled = false;   // the status strip says "stopping (releasing the trainer)" meanwhile
        await _ride.StopAsync();
        Close();
    }

    /// <summary>One number setting on a card: its name and help on the left, the number box (with - and +) on the right.</summary>
    private static NumericUpDown AddNumber(StackPanel card, string name, string title, double min, double max, int decimals, string help)
    {
        var n = new NumericUpDown
        {
            Name = name, Minimum = (decimal)min, Maximum = (decimal)max, DecimalPlaces = decimals,
            Increment = decimals == 0 ? 5 : decimals == 1 ? 0.5m : 0.01m, AccessibleName = title,
        };
        card.Controls.Add(new SettingRow(title, help, new FieldFrame(n, S(170))));
        return n;
    }
}
