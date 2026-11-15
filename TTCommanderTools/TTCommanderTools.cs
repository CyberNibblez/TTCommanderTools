using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Input;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGame.Extended.BitmapFonts;

namespace CyberNibblez.Blish.TTCommanderTools
{
    [Export(typeof(Module))]
    public class KegDespawnTimerModule : Module
    {
        private static readonly Logger Logger = Logger.GetLogger<KegDespawnTimerModule>();

        #region Module boilerplate
        internal SettingsManager SettingsManager => ModuleParameters.SettingsManager;
        internal ContentsManager ContentsManager => ModuleParameters.ContentsManager;
        internal DirectoriesManager DirectoriesManager => ModuleParameters.DirectoriesManager;
        internal Gw2ApiManager Gw2ApiManager => ModuleParameters.Gw2ApiManager;

        [ImportingConstructor]
        public KegDespawnTimerModule([Import("ModuleParameters")] ModuleParameters moduleParameters)
            : base(moduleParameters) { }
        #endregion

        // ---------------------------------------------------------------
        // Constants
        // ---------------------------------------------------------------
        private const double StartSeconds = 839;   // main timer starts at 14:00
        private const double DespawnDelay = 120;       // despawn lags the keg run by 2 minutes
        private const double MaxSeconds   = 60 * 60;

        private const int _soonThreshold = 30;

        // ---------------------------------------------------------------
        // Data
        // ---------------------------------------------------------------
        private class KegRun
        {
            public double Start;          // event-clock value when run started
            public double? Stop;          // event-clock value when run stopped (null = still running)

            public double DespawnStart => Start - DespawnDelay;
            public double? DespawnEnd  => Stop.HasValue ? Stop.Value - DespawnDelay : (double?)null;
        }

        private enum DespawnStatus { Start, Clear, Soon, Active }

        private double _remaining = StartSeconds;
        private bool _running;
        private readonly List<KegRun> _runs = new List<KegRun>();
        private KegRun _activeRun;
        private bool _listDirty = true;

        // ---------------------------------------------------------------
        // Settings
        // ---------------------------------------------------------------
        /*private SettingEntry<int> _soonThreshold;*/
        private SettingEntry<KeyBinding> _timerHotkey;
        private SettingEntry<KeyBinding> _kegHotkey;

        // ---------------------------------------------------------------
        // UI
        // ---------------------------------------------------------------
        private CornerIcon _cornerIcon;
        private StandardWindow _window;
        private Label _timerLabel;
        private StandardButton _startPauseButton;
        private StandardButton _kegButton;
        private Label _statusLabel;
        private Label _startLabel;
        private Label _endLabel;
        private FlowPanel _listPanel;
        /*private object _soonThreshold;*/
        MonoGame.Extended.BitmapFonts.BitmapFont TimerTimeFont = GameService.Content.GetFont(ContentService.FontFace.Menomonia, ContentService.FontSize.Size36, ContentService.FontStyle.Bold);
        private static readonly Color BaseColor  = new Color(255, 255, 255);
        private static readonly Color ClearColor  = new Color(90, 220, 90);
        private static readonly Color SoonColor   = new Color(255, 235, 40);
        private static readonly Color ActiveColor = new Color(255, 80, 60);

        private Texture2D _titleCobaltIconTexture;
        

        protected override void DefineSettings(SettingCollection settings)
        {
            /*_soonThreshold = settings.DefineSetting(
                "SoonThresholdSeconds", 120,
                () => "'Despawns Soon' warning (seconds)",
                () => "How many seconds before a despawn window starts the status changes to 'Despawns Soon'.");*/

            _timerHotkey = settings.DefineSetting(
                "TimerHotkey", new KeyBinding(Keys.None),
                () => "Start / Pause timer hotkey",
                () => "Optional hotkey to start or pause the main timer.");

            _kegHotkey = settings.DefineSetting(
                "KegRunHotkey", new KeyBinding(Keys.None),
                () => "Start / Stop keg run hotkey",
                () => "Optional hotkey to start or stop a keg run without clicking the window.");
        }

        protected override void Initialize()
        {
            BuildUi();
        }

        protected override void OnModuleLoaded(EventArgs e)
        {
            _timerHotkey.Value.Enabled = true;
            _timerHotkey.Value.Activated += OnTimerHotkey;
            _kegHotkey.Value.Enabled = true;
            _kegHotkey.Value.Activated += OnKegHotkey;

            base.OnModuleLoaded(e);
        }

        // ---------------------------------------------------------------
        // UI construction
        // ---------------------------------------------------------------
        private void BuildUi()
        {
            _titleCobaltIconTexture = ContentsManager.GetTexture("wurm_egg.png");

            _window = new StandardWindow(
                ContentsManager.GetTexture("window_bg.png"),
                new Rectangle(35, 50, 560, 250), // Window Region
                new Rectangle(35, 20, 540, 260)) // Content Region
            {
                Parent = GameService.Graphics.SpriteScreen,
                Title = "TT Tools",
                Subtitle = "Cobalt Keg Despawn Timer",
                SavesPosition = true,
                Id = "KegDespawnTimer_MainWindow",
                CanResize = false,
                Emblem = _titleCobaltIconTexture,
                Location = new Point(300, 200)
            };

            // Main countdown
            _timerLabel = new Label
            {
                Parent = _window,
                Text = FormatTime(_remaining),
                Font = TimerTimeFont,
                TextColor = Color.White,
                AutoSizeWidth = true,
                AutoSizeHeight = true,
                Location = new Point(8, 15)
            };

            _startPauseButton = MakeButton("Timer\nStart", new Point(100, 0), 60, 50);
            _startPauseButton.Click += (s, e) => ToggleTimer();

            var resetButton = MakeButton("Reset", new Point(170, 10), 60, 30);
            resetButton.Click += (s, e) => ResetAll();

            // +/- minute buttons
            MakeAdjustButton("<< 5s", 5, 270);
            MakeAdjustButton("<< 1s", 1, 340);
            MakeAdjustButton("1s >>", -1, 410);
            MakeAdjustButton("5s >>", -5, 480);

            // Big status + start/end
            _statusLabel = new Label
            {
                Parent = _window,
                Text = "Press Start ^",
                Font = TimerTimeFont,
                TextColor = ClearColor,
                AutoSizeWidth = true,
                AutoSizeHeight = true,
                Location = new Point(8, 60)
            };

            _startLabel = new Label
            {
                Parent = _window,
                Text = "Start: --:--",
                Font = GameService.Content.DefaultFont18,
                TextColor = ClearColor,
                AutoSizeWidth = true,
                AutoSizeHeight = true,
                Location = new Point(8, 110)
            };

            _endLabel = new Label
            {
                Parent = _window,
                Text = "End:   --:--",
                Font = GameService.Content.DefaultFont18,
                TextColor = ClearColor,
                AutoSizeWidth = true,
                AutoSizeHeight = true,
                Location = new Point(8, 140)
            };

            // Keg run list
            _listPanel = new FlowPanel
            {
                Parent = _window,
                Location = new Point(270, 60),
                Size = new Point(270, 170),
                FlowDirection = ControlFlowDirection.SingleTopToBottom,
                ControlPadding = new Vector2(0, 3),
                CanScroll = true,
                ShowBorder = true,
                Title = "Run Log  (Keg Run | Despawns)",
            };

            _kegButton = MakeButton("START Keg Run", new Point(8, 170), 250, 50);
            _kegButton.Click += (s, e) => ToggleKegRun();

            // Corner icon
            _cornerIcon = new CornerIcon
            {
                Icon = _titleCobaltIconTexture,
                BasicTooltipText = "TT Commander Tools"
            };
            _cornerIcon.Click += (s, e) => _window.ToggleWindow();
        }

        private StandardButton MakeButton(string text, Point location, int width, int height)
        {
            return new StandardButton
            {
                Parent = _window,
                Text = text,
                Location = location,
                Width = width,
                Height = height
            };
        }

        private void MakeAdjustButton(string text, double deltaSeconds, int x)
        {
            var b = MakeButton(text, new Point(x, 10), 60, 30);
            b.Click += (s, e) => Adjust(deltaSeconds);
        }

        // ---------------------------------------------------------------
        // Actions
        // ---------------------------------------------------------------
        private void ToggleTimer()
        {
            if (_remaining <= 0) _remaining = StartSeconds;
            _running = !_running;
        }

        private void ResetAll()
        {
            _running = false;
            _remaining = StartSeconds;
            _runs.Clear();
            _activeRun = null;
            _listDirty = true;
            _kegButton.Enabled = true;
        }

        private void Adjust(double deltaSeconds)
        {
            _remaining = MathHelper.Clamp((float)(_remaining + deltaSeconds), 0f, (float)MaxSeconds);
        }

        private void ToggleKegRun()
        {
            // Record whole seconds (exactly what the main timer shows) so the Start and End
            // countdowns share the same fractional offset and tick over together.
            double now = Math.Ceiling(_remaining);

            if (_activeRun == null)
            {
                if(!_running)
                {
                    ToggleTimer();
                }

                _activeRun = new KegRun { Start = now };
                _runs.Add(_activeRun);
            }
            else
            {
                _activeRun.Stop = now;
                _activeRun = null;
            }
            _listDirty = true;
        }

        private void OnTimerHotkey(object sender, EventArgs e) => ToggleTimer();
        private void OnKegHotkey(object sender, EventArgs e) => ToggleKegRun();

        // ---------------------------------------------------------------
        // Per-frame logic
        // ---------------------------------------------------------------
        protected override void Update(GameTime gameTime)
        {
            if (_window == null) return;

            if (_running)
            {
                

                _remaining -= gameTime.ElapsedGameTime.TotalSeconds;
                if (_remaining <= 0)
                {
                    _remaining = 0;
                    _running = false;

                    // Close any open keg run when the event timer expires
                    if (_activeRun != null)
                    {
                        _activeRun.Stop = 0;
                        _activeRun = null;
                        _listDirty = true;
                    }
                }
            }

            if (_remaining <= 120)
            {
                _kegButton.Enabled = false;

                // Close any open keg run when the event timer expires
                if (_activeRun != null)
                {
                    _activeRun.Stop = 0;
                    _activeRun = null;
                    _listDirty = true;
                }
            } else
            {
                _kegButton.Enabled = true;
            }

            RefreshUi();
        }

        private void RefreshUi()
        {
            _timerLabel.Text = FormatTime(_remaining);
            _startPauseButton.Text = _running ? "Timer\nPause" : "Timer\nStart";
            _kegButton.Text = _activeRun == null ? "START Keg Run" : "STOP Keg Run";

            if (_listDirty)
            {
                RebuildList();
                _listDirty = false;
            }

            KegRun focus;
            DespawnStatus status = Evaluate(out focus);

            Color color;
            switch (status)
            {
                case DespawnStatus.Active: _statusLabel.Text = "In Despawns";    color = ActiveColor; break;
                case DespawnStatus.Soon:   _statusLabel.Text = "Despawns Soon";  color = SoonColor;   break;
                case DespawnStatus.Clear:   _statusLabel.Text = "Despawns Clear";  color = ClearColor;   break;
                default:                   _statusLabel.Text = "Press ^^ Start"; color = BaseColor;  break;
            }

            _statusLabel.TextColor = color;
            _startLabel.TextColor = color;
            _endLabel.TextColor = color;

            if (focus == null)
            {
                _startLabel.Text = "";
                _endLabel.Text   = "";
                return;
            }

            // Seconds until the despawn window starts
            double untilStart = _remaining - focus.DespawnStart;
            _startLabel.Text = $"Start: {FormatTime(focus.DespawnStart)}   " +
                               (untilStart > 0 ? $"[{(int)Math.Ceiling(untilStart)}s]" : "[started]");

            if (focus.DespawnEnd.HasValue)
            {
                double untilEnd = _remaining - focus.DespawnEnd.Value;
                _endLabel.Text = $"End:   {FormatTime(focus.DespawnEnd.Value)}   " +
                                 $"[{Math.Max(0, (int)Math.Ceiling(untilEnd))}s]";
            }
            else
            {
                _endLabel.Text = "End:   --:--   [keg run active]";
            }
        }

        /// <summary>
        /// Works out the current status and which keg run the big display should focus on.
        /// Priority: a despawn window we are inside > the next upcoming window.
        /// </summary>
        private DespawnStatus Evaluate(out KegRun focus)
        {
            focus = null;
            if (_runs.Count == 0 && !_running)
            {
                return DespawnStatus.Start;
            }
            else if(_runs.Count == 0 && _running)
            {
                return DespawnStatus.Clear;
            }
            
            KegRun upcoming = null;

            foreach (var run in _runs)
            {
                bool started = _remaining <= run.DespawnStart;
                bool ended = run.DespawnEnd.HasValue && _remaining < run.DespawnEnd.Value;

                if (started && !ended)
                {
                    // Event timer already expired => nothing left to warn about
                    if (_remaining <= 0 && !_running && run.Stop.HasValue)
                    {
                        continue;
                    }
                    focus = run;
                    return DespawnStatus.Active;
                }

                if (!started)
                {
                    // Nearest upcoming = the one with the highest DespawnStart still ahead of us
                    if (upcoming == null || run.DespawnStart > upcoming.DespawnStart)
                        upcoming = run;
                }
            }

            if (upcoming == null) return DespawnStatus.Clear;

            focus = upcoming;
            double threshold = Math.Max(1, _soonThreshold);
            return (_remaining - upcoming.DespawnStart) <= threshold
                ? DespawnStatus.Soon
                : DespawnStatus.Clear;
        }

        private void RebuildList()
        {
            _listPanel.ClearChildren();

            int i = 1;
            foreach (var run in _runs)
            {
                string keg = $"{FormatTime(run.Start)} - {(run.Stop.HasValue ? FormatTime(run.Stop.Value) : "...")}";
                string des = $"{FormatTime(run.DespawnStart)} - {(run.DespawnEnd.HasValue ? FormatTime(run.DespawnEnd.Value) : "...")}";

                new Label
                {
                    Parent = _listPanel,
                    Text = $"#{i}    {keg}     |     {des}",
                    Font = GameService.Content.DefaultFont14,
                    TextColor = Color.White,
                    AutoSizeWidth = true,
                    AutoSizeHeight = true
                };
                i++;
            }
        }

        private static string FormatTime(double seconds)
        {
            int total = Math.Max(0, (int)Math.Ceiling(seconds));
            return $"{total / 60}:{total % 60:00}";
        }

        // ---------------------------------------------------------------
        // Cleanup
        // ---------------------------------------------------------------
        protected override void Unload()
        {
            if (_timerHotkey?.Value != null)
            {
                _timerHotkey.Value.Activated -= OnTimerHotkey;
                _timerHotkey.Value.Enabled = false;
            }
            if (_kegHotkey?.Value != null)
            {
                _kegHotkey.Value.Activated -= OnKegHotkey;
                _kegHotkey.Value.Enabled = false;
            }

            _cornerIcon?.Dispose();
            _window?.Dispose();
        }
    }
}
