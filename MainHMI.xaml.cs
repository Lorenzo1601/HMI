using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HMI.Function;
using HMI.Models;
using Microsoft.Win32;
using S7.Net;

namespace HMI;

public partial class MainWindow : Window
{
    private const double SnapSize = 5;
    private double _leftSidebarExpandedWidth = 248;
    private const double LeftSidebarMinimumWidth = 210;
    private const double LeftSidebarMaximumWidth = 420;
    private double _rightSidebarExpandedWidth = 330;
    private const double RightSidebarMinimumWidth = 280;
    private const double RightSidebarMaximumWidth = 560;
    private const double WorkspaceMinimumWidth = 520;
    private const double SidebarRailWidth = 28;
    private static readonly string[] ChartSeriesPalette = ["#28C2B8", "#227CFF", "#F1B24A", "#EF5B5B", "#7C5CFC", "#12B981", "#EC4899", "#F8FAFC"];
    private static readonly AnimationConditionOption[] AnimationConditionOptions =
    [
        new(HmiDynamicCondition.True, "Vero"),
        new(HmiDynamicCondition.False, "Falso"),
        new(HmiDynamicCondition.Equals, "Uguale a"),
        new(HmiDynamicCondition.NotEquals, "Diverso da"),
        new(HmiDynamicCondition.GreaterThan, "Maggiore di"),
        new(HmiDynamicCondition.GreaterThanOrEqual, "Maggiore o uguale"),
        new(HmiDynamicCondition.LessThan, "Minore di"),
        new(HmiDynamicCondition.LessThanOrEqual, "Minore o uguale"),
        new(HmiDynamicCondition.BetweenInclusive, "Intervallo incluso"),
        new(HmiDynamicCondition.BitSet, "Bit impostato"),
        new(HmiDynamicCondition.BitClear, "Bit non impostato"),
        new(HmiDynamicCondition.BitMaskEquals, "Maschera bit uguale")
    ];
    private readonly ProjectStorageService _storage = new();
    private readonly RuntimeExportService _runtimeExporter = new();
    private readonly HmiRuntimeSession _runtime = new();
    private readonly AlarmHistoryService _alarmHistory = new();
    private readonly UserSecurityService _userSecurity = new();
    private readonly UserSessionAuditService _userSessionAudit = new();
    private readonly RuntimeSecurityStoreService _runtimeSecurityStore = new();
    private readonly DispatcherTimer _userSessionTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, RuntimeWidgetBinding> _runtimeBindings = [];
    private readonly List<HmiWidgetDefinition> _displayedRuntimeWidgets = [];
    private readonly List<Window> _runtimePopups = [];
    private readonly Dictionary<string, AlarmRuntimeState> _activeAlarms = [];
    private readonly HashSet<string> _selectedWidgetIds = [];
    private readonly Dictionary<string, Point> _dragOrigins = [];

    private HmiProject _project = HmiProject.CreateStarter();
    private HmiPageDefinition? _selectedPage;
    private PageFolderDefinition? _selectedPageFolder;
    private HmiWidgetDefinition? _selectedWidget;
    private PlcConnectionDefinition? _editingPlc;
    private TagDefinition? _editingTag;
    private TagFolderDefinition? _selectedTagFolder;
    private RecipeBookDefinition? _editingRecipeBook;
    private AlarmDefinition? _editingAlarm;
    private AlarmFolderDefinition? _selectedAlarmFolder;
    private RedundantPanelDefinition? _editingRedundantPanel;
    private TagDefinition? _editingDatabaseTag;
    private UserDefinition? _editingUser;
    private string? _editingChartSeriesId;
    private AuthenticatedUserIdentity? _currentUser;
    private string? _currentUserAuditSessionId;
    private DateTime _lastUserActivityUtc = DateTime.UtcNow;
    private readonly SemaphoreSlim _runtimeProjectSaveLock = new(1, 1);
    private string? _projectPath;
    private bool _runtimeOnly;
    private bool _allowRuntimeClose;
    private bool _runtimeExitInProgress;
    private bool _dirty;
    private bool _runtimeMode;
    private bool _updatingInspector;
    private bool _pageFolderInspectorActive;
    private bool _dragging;
    private Point _dragStart;
    private bool _leftSidebarCollapsed;
    private bool _rightSidebarCollapsed;
    private string? _editingAnimationRuleId;

    public MainWindow()
    {
        InitializeComponent();
        PlcDriverCombo.ItemsSource = Enum.GetValues<PlcDriver>();
        TagDataTypeCombo.ItemsSource = Enum.GetValues<TagDataType>();
        TagAccessCombo.ItemsSource = Enum.GetValues<TagAccess>();
        PageTypeCombo.ItemsSource = Enum.GetValues<HmiPageType>();
        WidgetImageStretchCombo.ItemsSource = new[] { "Uniform", "UniformToFill", "Fill", "None" };
        WidgetAnimationRuleConditionCombo.ItemsSource = AnimationConditionOptions;
        AlarmConditionCombo.ItemsSource = Enum.GetValues<AlarmCondition>();
        AlarmSeverityCombo.ItemsSource = Enum.GetValues<AlarmSeverity>();
        DatabaseLoggingModeCombo.ItemsSource = Enum.GetValues<DatabaseLoggingMode>();
        WidgetChartSourceCombo.ItemsSource = Enum.GetValues<ChartDataSource>();
        PlcCpuCombo.ItemsSource = Enum.GetNames<CpuType>();
        _runtime.TagValueChanged += Runtime_TagValueChanged;
        _runtime.RedundancyStateChanged += Runtime_RedundancyStateChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewMouseDown += (_, _) => RecordRuntimeUserActivity();
        _userSessionTimer.Tick += UserSessionTimer_Tick;
        SizeChanged += MainWindow_SizeChanged;
        LoadProjectIntoEditor();
    }

    public MainWindow(string runtimeProjectPath) : this()
    {
        _runtimeOnly = true;
        _projectPath = runtimeProjectPath;
        Opacity = 0;
        Loaded += async (_, _) =>
        {
            try
            {
                _project = await _storage.LoadAsync(runtimeProjectPath);
                _project.Security = await _runtimeSecurityStore.LoadOrCloneAsync(runtimeProjectPath, _project.Name, _project.Security);
                _project.Normalize();
                _dirty = false;
                LoadProjectIntoEditor();
                ProjectCommandsPanel.Visibility = Visibility.Collapsed;
                ModeCommandsPanel.Visibility = Visibility.Collapsed;
                Title = _project.Name + " — Runtime";
                await StartRuntimeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Avvio runtime", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        };
    }

    private void LoadProjectIntoEditor()
    {
        _project.Normalize();
        _selectedPage = _project.Pages.FirstOrDefault(page => page.Id == _project.StartupPageId) ?? _project.Pages[0];
        ClearWidgetSelection();
        _editingUser = _project.Security.Users
            .OrderByDescending(user => user.AccessLevel)
            .ThenBy(user => user.Username)
            .FirstOrDefault();
        _currentUser = null;
        _currentUserAuditSessionId = null;
        _pageFolderInspectorActive = false;
        _editingAnimationRuleId = null;
        _editingChartSeriesId = null;
        RefreshCollections();
        if (_editingUser is null)
        {
            PopulateUserForm(new UserDefinition
            {
                AccessLevel = _project.Security.MaximumAccessLevel,
                IsActive = true
            });
        }
        RenderDesigner();
        UpdateProjectHeader();
        ShowWidgetInspector();
    }

    private void RefreshCollections()
    {
        var pageId = _selectedPage?.Id;
        var plcId = _editingPlc?.Id;
        var tagId = _editingTag?.Id;
        var recipeBookId = _editingRecipeBook?.Id;
        var alarmId = _editingAlarm?.Id;
        var panelId = _editingRedundantPanel?.Id;
        var userId = _editingUser?.Id;

        RefreshPageTree(_pageFolderInspectorActive ? null : pageId, _pageFolderInspectorActive ? _selectedPageFolder?.Id : null);

        PlcList.ItemsSource = null;
        PlcList.ItemsSource = _project.PlcConnections;
        PlcList.SelectedItem = _project.PlcConnections.FirstOrDefault(plc => plc.Id == plcId);

        RefreshTagTree(tagId, _editingTag is null ? _selectedTagFolder?.Id : null);

        RecipeTagList.ItemsSource = null;
        RecipeTagList.ItemsSource = _project.Tags;
        RecipeBookList.ItemsSource = null;
        RecipeBookList.ItemsSource = _project.RecipeBooks;
        RecipeBookList.SelectedItem = _project.RecipeBooks.FirstOrDefault(book => book.Id == recipeBookId);

        RefreshAlarmTree(alarmId, _editingAlarm is null ? _selectedAlarmFolder?.Id : null);

        RedundantPanelList.ItemsSource = null;
        RedundantPanelList.ItemsSource = _project.Redundancy.Panels.OrderBy(panel => panel.Priority).ToList();
        RedundantPanelList.SelectedItem = _project.Redundancy.Panels.FirstOrDefault(panel => panel.Id == panelId);

        UserList.ItemsSource = null;
        UserList.ItemsSource = _project.Security.Users.OrderByDescending(user => user.AccessLevel).ThenBy(user => user.Username).ToList();
        UserList.SelectedItem = _project.Security.Users.FirstOrDefault(user => user.Id == userId);

        _updatingInspector = true;
        WidgetTagCombo.ItemsSource = null;
        WidgetTagCombo.ItemsSource = _project.Tags;
        WidgetTargetPageCombo.ItemsSource = null;
        WidgetTargetPageCombo.ItemsSource = _project.Pages;
        WidgetRecipeBookCombo.ItemsSource = null;
        WidgetRecipeBookCombo.ItemsSource = _project.RecipeBooks;
        WidgetImageCombo.ItemsSource = null;
        WidgetImageCombo.ItemsSource = _project.Assets;
        WidgetAnimationTagCombo.ItemsSource = null;
        WidgetAnimationTagCombo.ItemsSource = _project.Tags.Where(tag => tag.Access != TagAccess.Write).ToList();
        WidgetChartSeriesTagCombo.ItemsSource = null;
        WidgetChartSeriesTagCombo.ItemsSource = _project.Tags.Where(IsChartCompatibleTag).ToList();
        TagPlcCombo.ItemsSource = null;
        TagPlcCombo.ItemsSource = _project.PlcConnections;
        TagFolderCombo.ItemsSource = null;
        TagFolderCombo.ItemsSource = BuildFolderChoices();
        AlarmTagCombo.ItemsSource = null;
        AlarmTagCombo.ItemsSource = _project.Tags;
        AlarmFolderCombo.ItemsSource = null;
        AlarmFolderCombo.ItemsSource = BuildAlarmFolderChoices();
        PageFolderCombo.ItemsSource = null;
        PageFolderCombo.ItemsSource = BuildPageFolderChoices();
        PageTemplateCombo.ItemsSource = null;
        PageTemplateCombo.ItemsSource = new[] { new HmiPageDefinition { Id = string.Empty, Name = "— Nessun template —", Type = HmiPageType.Template } }
            .Concat(_project.Pages.Where(page => page.Type == HmiPageType.Template)).ToList();
        DatabaseTagList.ItemsSource = null;
        DatabaseTagList.ItemsSource = _project.Tags;
        _updatingInspector = false;

        RedundancyEnabledCheck.IsChecked = _project.Redundancy.Enabled;
        RedundancyDelayBox.Text = _project.Redundancy.FailoverDelayMs.ToString(CultureInfo.InvariantCulture);
        RedundancyHealthBox.Text = _project.Redundancy.HealthCheckIntervalMs.ToString(CultureInfo.InvariantCulture);
        PopulateDatabaseForm();
        PopulateSecuritySettingsForm();

        if (_editingTag is not null)
        {
            PopulateTagForm(_editingTag);
        }
        if (_editingRecipeBook is not null)
        {
            RecipeBookNameBox.Text = _editingRecipeBook.Name;
            RecipeTagList.SelectedItems.Clear();
            foreach (var tag in _project.Tags.Where(tag => _editingRecipeBook.TagIds.Contains(tag.Id)))
            {
                RecipeTagList.SelectedItems.Add(tag);
            }
        }
        if (_editingAlarm is not null)
        {
            PopulateAlarmForm(_editingAlarm);
        }
        if (_editingRedundantPanel is not null)
        {
            PopulateRedundantPanelForm(_editingRedundantPanel);
        }
        if (_editingUser is not null)
        {
            PopulateUserForm(_editingUser);
        }

        ShowWidgetInspector();
        PopulatePageInspector();
        PopulatePageFolderInspector();
    }

    private void RenderDesigner()
    {
        if (_selectedPage is null)
        {
            return;
        }

        DesignCanvas.Children.Clear();
        DesignCanvas.Width = _selectedPage.Width;
        DesignCanvas.Height = _selectedPage.Height;
        DesignCanvas.Background = BrushOf(_selectedPage.Background, "#101821");
        CurrentPageText.Text = _selectedPage.Name;
        CurrentPageSizeText.Text = $"  /  Area {FormatNumber(_selectedPage.Width)} × {FormatNumber(_selectedPage.Height)} · {_selectedPage.Type}";

        if (_selectedPage.Type != HmiPageType.Template && !string.IsNullOrWhiteSpace(_selectedPage.TemplatePageId))
        {
            var template = _project.Pages.FirstOrDefault(page => page.Id == _selectedPage.TemplatePageId && page.Type == HmiPageType.Template);
            if (template is not null)
            {
                foreach (var templateWidget in template.Widgets)
                {
                    var preview = CreateWidgetVisual(templateWidget, false);
                    preview.Width = templateWidget.Width;
                    preview.Height = templateWidget.Height;
                    preview.Opacity = 0.72;
                    preview.IsHitTestVisible = false;
                    Canvas.SetLeft(preview, templateWidget.X);
                    Canvas.SetTop(preview, templateWidget.Y);
                    DesignCanvas.Children.Add(preview);
                }
            }
        }

        foreach (var widget in _selectedPage.Widgets)
        {
            var isPrimary = widget.Id == _selectedWidget?.Id;
            var isSelected = _selectedWidgetIds.Contains(widget.Id);
            var root = new Grid
            {
                Width = widget.Width,
                Height = widget.Height,
                Tag = widget,
                Cursor = Cursors.SizeAll,
                Background = Brushes.Transparent
            };

            var selectionBorder = new Border
            {
                Child = CreateWidgetVisual(widget, false),
                BorderBrush = isPrimary ? BrushOf("#28C2B8") : isSelected ? BrushOf("#227CFF") : Brushes.Transparent,
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(4)
            };
            selectionBorder.Child.IsHitTestVisible = false;
            root.Children.Add(selectionBorder);

            var grip = new Thumb
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = BrushOf("#28C2B8"),
                Cursor = Cursors.SizeNWSE,
                Visibility = isPrimary ? Visibility.Visible : Visibility.Collapsed,
                Tag = widget
            };
            grip.DragDelta += ResizeGrip_DragDelta;
            grip.DragCompleted += ResizeGrip_DragCompleted;
            root.Children.Add(grip);

            root.MouseLeftButtonDown += DesignerItem_MouseLeftButtonDown;
            root.MouseMove += DesignerItem_MouseMove;
            root.MouseLeftButtonUp += DesignerItem_MouseLeftButtonUp;
            Canvas.SetLeft(root, widget.X);
            Canvas.SetTop(root, widget.Y);
            DesignCanvas.Children.Add(root);
        }
        UpdateAlignmentCommandState();
    }

    private FrameworkElement CreateWidgetVisual(HmiWidgetDefinition widget, bool interactive)
    {
        var previewDynamicAppearance = !interactive && widget.Animation.Enabled && SupportsDynamicAppearance(widget.Type);
        var foreground = BrushOf(previewDynamicAppearance ? widget.Animation.DefaultForeground : widget.Foreground, "#F8FAFC");
        var background = BrushOf(previewDynamicAppearance ? widget.Animation.DefaultBackground : widget.Background, "#253244");
        var textAlignment = ResolveTextAlignment(widget);

        if (widget.Type == HmiWidgetType.Image)
        {
            var image = CreateAssetImage(widget.ImageAssetId, widget.ImageStretch);
            return new Border
            {
                Background = background,
                Child = (UIElement?)image ?? new TextBlock
                {
                    Text = "Doppio clic sulle proprietà per importare un'immagine",
                    Foreground = BrushOf("#8FA0B3"),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12)
                }
            };
        }

        if (widget.Type == HmiWidgetType.Label)
        {
            return new Border
            {
                Background = background,
                Padding = new Thickness(10, 5, 10, 5),
                Child = new TextBlock
                {
                    Text = widget.Text,
                    Foreground = foreground,
                    FontSize = widget.FontSize,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = textAlignment,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
        }

        if (widget.Type is HmiWidgetType.Button or HmiWidgetType.Navigation or HmiWidgetType.PopupButton or HmiWidgetType.PopupClose or HmiWidgetType.RuntimeExit or HmiWidgetType.LoginButton or HmiWidgetType.LogoutButton)
        {
            object content = widget.Text;
            if (widget.UseImageAsContent)
            {
                content = (object?)CreateAssetImage(widget.ImageAssetId, widget.ImageStretch) ?? new TextBlock { Text = widget.Text };
            }
            var button = new Button
            {
                Content = content,
                Background = background,
                Foreground = foreground,
                BorderThickness = new Thickness(0),
                FontSize = widget.FontSize,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Padding = new Thickness(12),
                HorizontalContentAlignment = ToHorizontalAlignment(textAlignment),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            if (interactive)
            {
                if (widget.Type == HmiWidgetType.LoginButton)
                {
                    button.Click += async (_, _) => await RequestRuntimeLoginAsync();
                }
                else if (widget.Type == HmiWidgetType.LogoutButton)
                {
                    button.Click += async (_, _) =>
                    {
                        if (EnsureWidgetAccess(widget, "logout"))
                        {
                            await LogoutCurrentUserAsync(UserSessionEndReason.ManualLogout);
                        }
                    };
                }
                else if (widget.Type == HmiWidgetType.Navigation)
                {
                    button.Click += (_, _) =>
                    {
                        if (EnsureWidgetAccess(widget, "navigazione"))
                        {
                            RenderRuntimePage(widget.TargetPageId);
                        }
                    };
                }
                else if (widget.Type == HmiWidgetType.PopupButton)
                {
                    button.Click += (_, _) =>
                    {
                        if (EnsureWidgetAccess(widget, "apertura popup"))
                        {
                            ToggleRuntimePopup(widget.TargetPageId);
                        }
                    };
                }
                else if (widget.Type == HmiWidgetType.PopupClose)
                {
                    button.Click += (_, _) =>
                    {
                        if (EnsureWidgetAccess(widget, "chiusura popup"))
                        {
                            CloseLastRuntimePopup();
                        }
                    };
                }
                else if (widget.Type == HmiWidgetType.RuntimeExit)
                {
                    button.Click += async (_, _) =>
                    {
                        if (EnsureWidgetAccess(widget, "uscita dal runtime"))
                        {
                            await RequestRuntimeExitAsync();
                        }
                    };
                }
                else
                {
                    button.Click += async (_, _) => await WriteWidgetValueAsync(widget, widget.WriteValue);
                }
            }
            return button;
        }

        if (widget.Type == HmiWidgetType.ValueDisplay)
        {
            var valueText = new TextBlock
            {
                Text = "—" + widget.Suffix,
                Foreground = foreground,
                FontSize = Math.Max(24, widget.FontSize * 1.9),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = textAlignment,
                TextWrapping = TextWrapping.NoWrap
            };
            var content = new Grid();
            if (widget.ShowDescription && !string.IsNullOrWhiteSpace(widget.Text))
            {
                content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.25, GridUnitType.Star) });
                content.Children.Add(CreateFittedTextView(new TextBlock
                {
                    Text = widget.Text,
                    Foreground = BrushOf("#8FA0B3"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = textAlignment,
                    TextWrapping = TextWrapping.NoWrap
                }, textAlignment));
                var valueView = CreateFittedTextView(valueText, textAlignment);
                Grid.SetRow(valueView, 1);
                content.Children.Add(valueView);
            }
            else
            {
                content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                content.Children.Add(CreateFittedTextView(valueText, textAlignment));
            }
            var border = NumericCardBorder(widget, background, content);
            if (interactive)
            {
                _runtimeBindings[widget.Id] = new RuntimeWidgetBinding { ValueText = valueText };
            }
            return border;
        }

        if (widget.Type == HmiWidgetType.Indicator)
        {
            var lamp = new Ellipse
            {
                Width = 100,
                Height = 100,
                Fill = BrushOf(widget.Animation.DefaultBackground, "#526273"),
                Stroke = BrushOf("#718196"),
                StrokeThickness = 3
            };
            var visual = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.Both,
                Child = lamp,
                Margin = new Thickness(2)
            };
            if (interactive)
            {
                _runtimeBindings[widget.Id] = new RuntimeWidgetBinding { Indicator = lamp };
            }
            return visual;
        }

        if (widget.Type == HmiWidgetType.RecipeManager)
        {
            return CreateRecipeManagerVisual(widget, interactive, background, foreground);
        }

        if (widget.Type == HmiWidgetType.UserManager)
        {
            return CreateUserManagerVisual(widget, interactive, background, foreground);
        }

        if (widget.Type == HmiWidgetType.AlarmViewer)
        {
            return CreateAlarmViewerVisual(widget, interactive, background, foreground);
        }

        if (widget.Type == HmiWidgetType.AlarmHistoryViewer)
        {
            return CreateAlarmHistoryViewerVisual(widget, interactive, background, foreground);
        }

        if (widget.Type == HmiWidgetType.DataHistoryViewer)
        {
            return CreateDataHistoryViewerVisual(widget, interactive, background, foreground);
        }

        if (widget.Type == HmiWidgetType.TrendChart)
        {
            return CreateTrendChartVisual(widget, interactive, background, foreground);
        }

        var input = new TextBox
        {
            Text = "0",
            Background = widget.ShowBackground ? BrushOf("#0D151E") : Brushes.Transparent,
            Foreground = foreground,
            BorderBrush = widget.ShowBackground ? BrushOf("#3B4C5E") : Brushes.Transparent,
            BorderThickness = widget.ShowBackground ? new Thickness(1) : new Thickness(0),
            FontSize = Math.Max(18, widget.FontSize),
            Padding = new Thickness(
                Math.Clamp(widget.Width * 0.035, 2, 10),
                Math.Clamp(widget.Height * 0.025, 0, 5),
                Math.Clamp(widget.Width * 0.035, 2, 10),
                Math.Clamp(widget.Height * 0.025, 0, 5)),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = textAlignment
        };
        AttachNumericInputAutoFit(input, Math.Max(18, widget.FontSize));
        if (interactive)
        {
            input.KeyDown += async (_, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    if (EnsureWidgetAccess(widget, "scrittura valore"))
                    {
                        await WriteWidgetValueAsync(widget, input.Text);
                    }
                    Keyboard.ClearFocus();
                }
            };
            _runtimeBindings[widget.Id] = new RuntimeWidgetBinding { Input = input };
        }
        var inputContent = new Grid();
        if (widget.ShowDescription && !string.IsNullOrWhiteSpace(widget.Text))
        {
            inputContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            inputContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.1, GridUnitType.Star) });
            inputContent.Children.Add(CreateFittedTextView(new TextBlock
            {
                Text = widget.Text,
                Foreground = BrushOf("#8FA0B3"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = textAlignment,
                TextWrapping = TextWrapping.NoWrap
            }, textAlignment));
            Grid.SetRow(input, 1);
        }
        else
        {
            inputContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }
        inputContent.Children.Add(input);
        return NumericCardBorder(widget, background, inputContent);
    }

    private Image? CreateAssetImage(string assetId, string stretchName)
    {
        var asset = _project.Assets.FirstOrDefault(item => item.Id == assetId);
        if (asset is null || string.IsNullOrWhiteSpace(asset.DataBase64))
        {
            return null;
        }
        try
        {
            var bytes = Convert.FromBase64String(asset.DataBase64);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return new Image
            {
                Source = bitmap,
                Stretch = Enum.TryParse<Stretch>(stretchName, out var stretch) ? stretch : Stretch.Uniform
            };
        }
        catch
        {
            return null;
        }
    }

    private FrameworkElement CreateUserManagerVisual(HmiWidgetDefinition widget, bool interactive, Brush background, Brush foreground)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(widget.Text) ? "GESTIONE UTENTI" : widget.Text,
            Foreground = foreground,
            FontSize = Math.Max(15, widget.FontSize),
            FontWeight = FontWeights.SemiBold
        };
        var sessionStatus = new TextBlock
        {
            Foreground = BrushOf("#28C2B8"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(sessionStatus, 1);
        header.Children.Add(sessionStatus);

        var usersList = new ListBox
        {
            MinHeight = 105,
            Margin = new Thickness(0, 7, 0, 7),
            DisplayMemberPath = nameof(UserDefinition.RuntimeSummary)
        };
        var usernameBox = new TextBox { ToolTip = "Nome utilizzato per il login" };
        var displayNameBox = new TextBox { ToolTip = "Nome mostrato nel runtime" };
        var accessLevelBox = new TextBox { ToolTip = $"Livello da 0 a {_project.Security.MaximumAccessLevel}" };
        var activeCheck = new CheckBox { Content = "Utente attivo", Foreground = foreground, Margin = new Thickness(0, 6, 0, 4) };
        var passwordBox = RuntimePasswordBox("Nuova password");
        var passwordConfirmBox = RuntimePasswordBox("Conferma password");
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition());
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.Children.Add(RuntimeFilterField("Username", usernameBox));
        var displayField = RuntimeFilterField("Nome", displayNameBox);
        Grid.SetColumn(displayField, 2);
        form.Children.Add(displayField);
        var accessField = RuntimeFilterField("Livello", accessLevelBox);
        Grid.SetRow(accessField, 1);
        form.Children.Add(accessField);
        Grid.SetRow(activeCheck, 1);
        Grid.SetColumn(activeCheck, 2);
        form.Children.Add(activeCheck);
        var passwordGrid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        passwordGrid.ColumnDefinitions.Add(new ColumnDefinition());
        passwordGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        passwordGrid.ColumnDefinitions.Add(new ColumnDefinition());
        passwordGrid.Children.Add(RuntimeFilterField("Password nuova", passwordBox));
        var passwordConfirmField = RuntimeFilterField("Conferma", passwordConfirmBox);
        Grid.SetColumn(passwordConfirmField, 2);
        passwordGrid.Children.Add(passwordConfirmField);
        var userActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var newButton = RuntimeActionButton("NUOVO", "#263646");
        var saveButton = RuntimeActionButton("SALVA", "#227CFF");
        var deleteButton = RuntimeActionButton("ELIMINA", "#3A2026");
        userActions.Children.Add(newButton);
        userActions.Children.Add(saveButton);
        userActions.Children.Add(deleteButton);
        var usersColumn = new Grid { Margin = new Thickness(0, 0, 10, 0) };
        usersColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });
        usersColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersColumn.Children.Add(usersList);
        Grid.SetRow(form, 1);
        usersColumn.Children.Add(form);
        Grid.SetRow(passwordGrid, 2);
        usersColumn.Children.Add(passwordGrid);
        Grid.SetRow(userActions, 3);
        usersColumn.Children.Add(userActions);

        var auditGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Margin = new Thickness(10, 7, 0, 0)
        };
        auditGrid.Columns.Add(new DataGridTextColumn { Header = "Utente", Binding = new System.Windows.Data.Binding(nameof(UserSessionRuntimeRow.User)) });
        auditGrid.Columns.Add(new DataGridTextColumn { Header = "Livello", Binding = new System.Windows.Data.Binding(nameof(UserSessionRuntimeRow.AccessLevel)), Width = 65 });
        auditGrid.Columns.Add(new DataGridTextColumn { Header = "Login", Binding = new System.Windows.Data.Binding(nameof(UserSessionRuntimeRow.Login)), Width = 125 });
        auditGrid.Columns.Add(new DataGridTextColumn { Header = "Logout", Binding = new System.Windows.Data.Binding(nameof(UserSessionRuntimeRow.Logout)), Width = 125 });
        auditGrid.Columns.Add(new DataGridTextColumn { Header = "Motivo", Binding = new System.Windows.Data.Binding(nameof(UserSessionRuntimeRow.Reason)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });
        content.Children.Add(usersColumn);
        Grid.SetColumn(auditGrid, 1);
        content.Children.Add(auditGrid);
        var accessDenied = new Border
        {
            Background = BrushOf("#101923"),
            BorderBrush = BrushOf("#3A4B5D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "🔒",
                        FontSize = 28,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 8)
                    },
                    new TextBlock
                    {
                        Text = "Accesso alla gestione utenti non autorizzato",
                        Foreground = BrushOf("#C9D4E0"),
                        FontWeight = FontWeights.SemiBold,
                        TextAlignment = TextAlignment.Center
                    }
                }
            }
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        Grid.SetRow(accessDenied, 1);
        root.Children.Add(accessDenied);
        var border = CardBorder(background, root);
        var binding = new RuntimeWidgetBinding
        {
            UserList = usersList,
            UserUsernameBox = usernameBox,
            UserDisplayNameBox = displayNameBox,
            UserAccessLevelBox = accessLevelBox,
            UserActiveCheck = activeCheck,
            UserPasswordBox = passwordBox,
            UserPasswordConfirmBox = passwordConfirmBox,
            UserAuditGrid = auditGrid,
            UserSessionStatus = sessionStatus,
            UserProtectedContent = content,
            UserAccessDeniedContent = accessDenied
        };
        if (interactive)
        {
            _runtimeBindings[widget.Id] = binding;
            usersList.SelectionChanged += (_, _) => PopulateRuntimeUserEditor(binding, usersList.SelectedItem as UserDefinition);
            newButton.Click += (_, _) => PopulateRuntimeUserEditor(binding, null);
            saveButton.Click += async (_, _) => await SaveRuntimeUserAsync(widget, binding);
            deleteButton.Click += async (_, _) => await DeleteRuntimeUserAsync(widget, binding);
        }
        RefreshRuntimeUserManager(binding);
        return border;
    }

    private static PasswordBox RuntimePasswordBox(string toolTip) => new()
    {
        Background = BrushOf("#0D151E"),
        Foreground = BrushOf("#F8FAFC"),
        BorderBrush = BrushOf("#263545"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 6, 8, 6),
        MinHeight = 32,
        ToolTip = toolTip
    };

    private FrameworkElement CreateRecipeManagerVisual(HmiWidgetDefinition widget, bool interactive, Brush background, Brush foreground)
    {
        var book = _project.RecipeBooks.FirstOrDefault(item => item.Id == widget.RecipeBookId);
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(widget.Text) ? "GESTIONE RICETTE" : widget.Text,
            Foreground = foreground,
            FontSize = Math.Max(15, widget.FontSize),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var combo = new ComboBox
        {
            ItemsSource = book?.Recipes,
            DisplayMemberPath = "Name",
            MinWidth = 210,
            Margin = new Thickness(0, 10, 0, 10)
        };
        if (book?.Recipes.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        var valuesPanel = new StackPanel();
        var valuesScroll = new ScrollViewer
        {
            Content = valuesPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 9)
        };
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        var loadButton = RuntimeActionButton("SCRIVI SU PLC", "#227CFF");
        var captureButton = RuntimeActionButton("LEGGI DA PLC", "#263646");
        var renameButton = RuntimeActionButton("RINOMINA", "#263646");
        var addButton = RuntimeActionButton("＋", "#263646");
        var deleteButton = RuntimeActionButton("−", "#3A2026");
        actionRow.Children.Add(loadButton);
        actionRow.Children.Add(captureButton);
        actionRow.Children.Add(renameButton);
        actionRow.Children.Add(addButton);
        actionRow.Children.Add(deleteButton);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(title);
        Grid.SetRow(combo, 1);
        grid.Children.Add(combo);
        Grid.SetRow(valuesScroll, 2);
        grid.Children.Add(valuesScroll);
        Grid.SetRow(actionRow, 3);
        grid.Children.Add(actionRow);

        var border = CardBorder(background, grid);
        var binding = new RuntimeWidgetBinding
        {
            RecipeBook = book,
            RecipeCombo = combo,
            RecipeValuesPanel = valuesPanel
        };

        if (interactive)
        {
            _runtimeBindings[widget.Id] = binding;
            combo.SelectionChanged += (_, _) => RefreshRecipeBinding(binding);
            loadButton.Click += async (_, _) => await LoadSelectedRecipeAsync(binding);
            captureButton.Click += async (_, _) => await CaptureSelectedRecipeAsync(binding);
            renameButton.Click += async (_, _) => await RenameRuntimeRecipeAsync(binding);
            addButton.Click += async (_, _) => await AddRuntimeRecipeAsync(binding);
            deleteButton.Click += async (_, _) => await DeleteRuntimeRecipeAsync(binding);
        }
        RefreshRecipeBinding(binding);
        return border;
    }

    private FrameworkElement CreateAlarmViewerVisual(HmiWidgetDefinition widget, bool interactive, Brush background, Brush foreground)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(widget.Text) ? "ALLARMI ATTIVI" : widget.Text,
            Foreground = foreground,
            FontSize = Math.Max(15, widget.FontSize),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var acknowledge = RuntimeActionButton("RICONOSCI TUTTI", "#263646");
        acknowledge.HorizontalAlignment = HorizontalAlignment.Right;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(acknowledge, 1);
        header.Children.Add(acknowledge);

        var searchBox = new TextBox { MinWidth = 180, ToolTip = "Filtra per nome, messaggio o cartella" };
        var severityCombo = new ComboBox { ItemsSource = new[] { "Tutte", "Info", "Warning", "Critical" }, SelectedIndex = 0, MinWidth = 110 };
        var stateCombo = new ComboBox { ItemsSource = new[] { "Tutti", "Attivi", "Rientrati" }, SelectedIndex = 0, MinWidth = 110 };
        var filters = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        filters.Children.Add(RuntimeFilterField("Cerca", searchBox));
        filters.Children.Add(RuntimeFilterField("Gravità", severityCombo));
        filters.Children.Add(RuntimeFilterField("Stato", stateCombo));

        var alarmsPanel = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = alarmsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(header);
        Grid.SetRow(filters, 1);
        grid.Children.Add(filters);
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);
        var border = CardBorder(background, grid);

        var binding = new RuntimeWidgetBinding
        {
            AlarmPanel = alarmsPanel,
            AlarmSearchBox = searchBox,
            AlarmSeverityCombo = severityCombo,
            AlarmStateCombo = stateCombo
        };
        if (interactive)
        {
            _runtimeBindings[widget.Id] = binding;
            acknowledge.Click += (_, _) =>
            {
                if (EnsureWidgetAccess(widget, "riconoscimento allarmi"))
                {
                    AcknowledgeAllAlarms();
                }
            };
            searchBox.TextChanged += (_, _) => RefreshAlarmBindings();
            severityCombo.SelectionChanged += (_, _) => RefreshAlarmBindings();
            stateCombo.SelectionChanged += (_, _) => RefreshAlarmBindings();
            RefreshAlarmBindings();
        }
        else
        {
            alarmsPanel.Children.Add(CreateAlarmRow("ESEMPIO", "Nessun allarme attivo", "#526273", DateTime.Now, null));
        }
        return border;
    }

    private FrameworkElement CreateAlarmHistoryViewerVisual(HmiWidgetDefinition widget, bool interactive, Brush background, Brush foreground)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(widget.Text) ? "STORICO ALLARMI" : widget.Text,
            Foreground = foreground,
            FontSize = Math.Max(15, widget.FontSize),
            FontWeight = FontWeights.SemiBold
        };
        var searchBox = new TextBox { MinWidth = 180, ToolTip = "Filtra nome, messaggio o cartella" };
        var severityCombo = new ComboBox { ItemsSource = new[] { "Tutte", "Info", "Warning", "Critical" }, SelectedIndex = 0, MinWidth = 105 };
        var stateCombo = new ComboBox { ItemsSource = new[] { "Tutti", "Aperti", "Risolti" }, SelectedIndex = 0, MinWidth = 100 };
        var fromPicker = new DatePicker { SelectedDate = DateTime.Today.AddDays(-7), Width = 125 };
        var toPicker = new DatePicker { SelectedDate = DateTime.Today, Width = 125 };
        var filters = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        filters.Children.Add(RuntimeFilterField("Cerca", searchBox));
        filters.Children.Add(RuntimeFilterField("Gravità", severityCombo));
        filters.Children.Add(RuntimeFilterField("Stato", stateCombo));
        filters.Children.Add(RuntimeFilterField("Dal", fromPicker));
        filters.Children.Add(RuntimeFilterField("Al", toPicker));
        var panel = new StackPanel();
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(title);
        Grid.SetRow(filters, 1);
        grid.Children.Add(filters);
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);
        var border = CardBorder(background, grid);
        var binding = new RuntimeWidgetBinding
        {
            AlarmHistoryPanel = panel,
            AlarmSearchBox = searchBox,
            AlarmSeverityCombo = severityCombo,
            AlarmStateCombo = stateCombo,
            AlarmFromPicker = fromPicker,
            AlarmToPicker = toPicker,
            AlarmHistoryRetentionDays = widget.AlarmHistoryRetentionDays
        };
        if (interactive)
        {
            _runtimeBindings[widget.Id] = binding;
            searchBox.TextChanged += (_, _) => RefreshAlarmHistoryBindings();
            severityCombo.SelectionChanged += (_, _) => RefreshAlarmHistoryBindings();
            stateCombo.SelectionChanged += (_, _) => RefreshAlarmHistoryBindings();
            fromPicker.SelectedDateChanged += (_, _) => RefreshAlarmHistoryBindings();
            toPicker.SelectedDateChanged += (_, _) => RefreshAlarmHistoryBindings();
            RefreshAlarmHistoryBindings();
        }
        else
        {
            panel.Children.Add(CreateAlarmRow("STORICO", "Esempio allarme risolto", "#526273", DateTime.Now.AddMinutes(-2), DateTime.Now));
        }
        return border;
    }

    private FrameworkElement CreateDataHistoryViewerVisual(HmiWidgetDefinition widget, bool interactive, Brush background, Brush foreground)
    {
        var historyToLocal = DateTime.Now;
        var historyFromLocal = historyToLocal.AddHours(-widget.HistoryHours);
        var databaseCombo = new ComboBox { MinWidth = 150 };
        var tableCombo = new ComboBox { MinWidth = 150 };
        var searchBox = new TextBox { MinWidth = 150, ToolTip = "Tag, valore o testo" };
        var fromPicker = new DatePicker { SelectedDate = historyFromLocal.Date, Width = 125, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Data iniziale" };
        var fromTimeBox = new TextBox { Text = historyFromLocal.ToString("HH:mm", CultureInfo.InvariantCulture), Width = 62, ToolTip = "Ora iniziale (HH:mm)" };
        var toPicker = new DatePicker { SelectedDate = historyToLocal.Date, Width = 125, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Data finale" };
        var toTimeBox = new TextBox { Text = historyToLocal.ToString("HH:mm", CultureInfo.InvariantCulture), Width = 62, ToolTip = "Ora finale (HH:mm)" };
        var limitBox = new TextBox { Text = widget.HistoryMaxRows.ToString(CultureInfo.InvariantCulture), Width = 70, ToolTip = "Righe massime" };
        var refresh = RuntimeActionButton("AGGIORNA", "#227CFF");
        refresh.VerticalAlignment = VerticalAlignment.Bottom;
        refresh.Margin = new Thickness(0, 18, 0, 0);
        var fromEditor = new StackPanel { Orientation = Orientation.Horizontal };
        fromEditor.Children.Add(fromPicker);
        fromEditor.Children.Add(fromTimeBox);
        var toEditor = new StackPanel { Orientation = Orientation.Horizontal };
        toEditor.Children.Add(toPicker);
        toEditor.Children.Add(toTimeBox);
        var filters = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        filters.Children.Add(RuntimeFilterField("Database", databaseCombo));
        filters.Children.Add(RuntimeFilterField("Tabella", tableCombo));
        filters.Children.Add(RuntimeFilterField("Cerca", searchBox));
        filters.Children.Add(RuntimeFilterField("Dal", fromEditor));
        filters.Children.Add(RuntimeFilterField("Al", toEditor));
        filters.Children.Add(RuntimeFilterField("Righe max", limitBox));
        filters.Children.Add(refresh);
        var status = new TextBlock { Text = interactive ? "Selezionare database e tabella" : "Anteprima storico dati", Foreground = BrushOf("#8FA0B3"), FontSize = 10 };
        var dataGrid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = BrushOf("#0D151E"),
            Foreground = BrushOf("#E8EEF5"),
            BorderBrush = BrushOf("#334354"),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            EnableRowVirtualization = true
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(widget.Text) ? "STORICO DATI" : widget.Text, Foreground = foreground, FontSize = Math.Max(15, widget.FontSize), FontWeight = FontWeights.SemiBold });
        Grid.SetRow(filters, 1);
        grid.Children.Add(filters);
        Grid.SetRow(status, 2);
        grid.Children.Add(status);
        Grid.SetRow(dataGrid, 3);
        grid.Children.Add(dataGrid);
        var border = CardBorder(background, grid);
        if (interactive)
        {
            var binding = new RuntimeWidgetBinding
            {
                HistoryDatabaseCombo = databaseCombo,
                HistoryTableCombo = tableCombo,
                HistorySearchBox = searchBox,
                HistoryFromPicker = fromPicker,
                HistoryFromTimeBox = fromTimeBox,
                HistoryToPicker = toPicker,
                HistoryToTimeBox = toTimeBox,
                HistoryLimitBox = limitBox,
                HistoryGrid = dataGrid,
                HistoryStatus = status
            };
            _runtimeBindings[widget.Id] = binding;
            databaseCombo.SelectionChanged += async (_, _) => await LoadHistoryTablesAsync(widget, binding);
            refresh.Click += async (_, _) => await QueryHistoryAsync(widget, binding);
            border.Loaded += async (_, _) => await LoadHistoryDatabasesAsync(widget, binding);
        }
        return border;
    }

    private FrameworkElement CreateTrendChartVisual(HmiWidgetDefinition widget, bool interactive, Brush background, Brush foreground)
    {
        var title = new TextBlock { Text = string.IsNullOrWhiteSpace(widget.Text) ? "GRAFICO" : widget.Text, Foreground = foreground, FontSize = Math.Max(15, widget.FontSize), FontWeight = FontWeights.SemiBold };
        var status = new TextBlock
        {
            Text = widget.ChartSeries.Count == 0
                ? "Nessuna serie configurata"
                : widget.ChartSource == ChartDataSource.LivePlc ? $"Dati live PLC · {widget.ChartSeries.Count} serie" : $"Dati storici MySQL · {widget.ChartSeries.Count} serie",
            Foreground = BrushOf("#8FA0B3"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var series in widget.ChartSeries)
        {
            var tagName = _project.Tags.FirstOrDefault(tag => tag.Id == series.TagId)?.Name ?? "Tag non disponibile";
            legend.Children.Add(CreateChartLegendItem(series, tagName));
        }
        if (widget.ChartSeries.Count == 0)
        {
            legend.Children.Add(new TextBlock { Text = "Aggiungere almeno una serie dalle proprietà del grafico", Foreground = BrushOf("#718397"), FontSize = 10 });
        }
        var legendScroll = new ScrollViewer
        {
            Content = legend,
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 38
        };
        var canvas = new Canvas { Background = BrushOf("#0D151E"), ClipToBounds = true, Margin = new Thickness(0, 10, 0, 0) };
        var gridLines = Enumerable.Range(0, 10).Select(_ => new Line
        {
            Stroke = BrushOf("#2A3948"),
            StrokeThickness = 1,
            Opacity = 0.7,
            IsHitTestVisible = false
        }).ToList();
        foreach (var gridLine in gridLines)
        {
            canvas.Children.Add(gridLine);
        }
        var runtimeSeries = new List<RuntimeChartSeriesBinding>();
        foreach (var series in widget.ChartSeries)
        {
            var line = new Polyline
            {
                Stroke = BrushOf(series.Color, "#28C2B8"),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            canvas.Children.Add(line);
            runtimeSeries.Add(new RuntimeChartSeriesBinding { Definition = series, Line = line });
        }
        var maxLabel = ChartAxisLabel();
        var minLabel = ChartAxisLabel();
        var startTimeLabel = ChartAxisLabel();
        var endTimeLabel = ChartAxisLabel();
        endTimeLabel.Width = 120;
        endTimeLabel.TextAlignment = TextAlignment.Right;
        canvas.Children.Add(maxLabel);
        canvas.Children.Add(minLabel);
        canvas.Children.Add(startTimeLabel);
        canvas.Children.Add(endTimeLabel);
        var refresh = RuntimeActionButton("AGGIORNA STORICO", "#263646");
        refresh.HorizontalAlignment = HorizontalAlignment.Right;
        refresh.Visibility = widget.ChartSource == ChartDataSource.HistoricalDatabase ? Visibility.Visible : Visibility.Collapsed;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(header);
        Grid.SetRow(legendScroll, 1);
        grid.Children.Add(legendScroll);
        Grid.SetRow(canvas, 2);
        grid.Children.Add(canvas);
        Grid.SetRow(refresh, 3);
        grid.Children.Add(refresh);
        var border = CardBorder(background, grid);
        var binding = new RuntimeWidgetBinding
        {
            ChartCanvas = canvas,
            ChartStatus = status,
            ChartSeriesBindings = runtimeSeries,
            ChartGridLines = gridLines,
            ChartMaxLabel = maxLabel,
            ChartMinLabel = minLabel,
            ChartStartTimeLabel = startTimeLabel,
            ChartEndTimeLabel = endTimeLabel
        };
        canvas.SizeChanged += (_, _) => RenderTrendChart(widget, binding);
        if (interactive)
        {
            _runtimeBindings[widget.Id] = binding;
            refresh.Click += async (_, _) => await LoadHistoricalTrendAsync(widget, binding);
            if (widget.ChartSource == ChartDataSource.HistoricalDatabase)
            {
                border.Loaded += async (_, _) => await LoadHistoricalTrendAsync(widget, binding);
            }
        }
        else
        {
            for (var seriesIndex = 0; seriesIndex < runtimeSeries.Count; seriesIndex++)
            {
                var phase = seriesIndex * 1.4;
                runtimeSeries[seriesIndex].Points = Enumerable.Range(0, 30)
                    .Select(index => new HistoryDataPoint(
                        DateTime.UtcNow.AddSeconds(index - 30),
                        40 + seriesIndex * 12 + Math.Sin(index / 4d + phase) * (12 + seriesIndex * 2)))
                    .ToList();
            }
        }
        return border;
    }

    private static FrameworkElement CreateChartLegendItem(ChartSeriesDefinition series, string tagName)
    {
        var displayName = string.IsNullOrWhiteSpace(series.DisplayName) ? tagName : series.DisplayName;
        var marker = new Border
        {
            Width = 18,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = BrushOf(series.Color, "#28C2B8"),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var text = new TextBlock
        {
            Text = displayName,
            Foreground = BrushOf("#C9D4E0"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{displayName}\nTag: {tagName}"
        };
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 5) };
        item.Children.Add(marker);
        item.Children.Add(text);
        return item;
    }

    private async Task LoadHistoryDatabasesAsync(HmiWidgetDefinition widget, RuntimeWidgetBinding binding)
    {
        if (binding.HistoryDatabaseCombo is null || binding.HistoryStatus is null)
        {
            return;
        }
        if (!HasWidgetAccess(widget))
        {
            binding.HistoryStatus.Text = $"Accesso richiesto: livello {widget.RequiredAccessLevel}";
            if (binding.HistoryGrid is not null)
            {
                binding.HistoryGrid.ItemsSource = null;
            }
            return;
        }
        try
        {
            binding.HistoryStatus.Text = "Caricamento database…";
            var requestVersion = ++binding.HistoryDatabaseRequestVersion;
            var databases = await new MySqlDatabaseService().GetDatabasesAsync(_project.Database);
            if (requestVersion != binding.HistoryDatabaseRequestVersion || !HasWidgetAccess(widget))
            {
                return;
            }
            binding.HistoryDatabaseCombo.ItemsSource = databases;
            var preferred = string.IsNullOrWhiteSpace(widget.HistoryDatabaseName) ? _project.Database.DatabaseName : widget.HistoryDatabaseName;
            binding.HistoryDatabaseCombo.SelectedItem = databases.FirstOrDefault(name => name.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? databases.FirstOrDefault();
            binding.HistoryStatus.Text = $"{databases.Count} database disponibili";
        }
        catch (Exception ex)
        {
            binding.HistoryStatus.Text = "Database non disponibile: " + ex.Message;
        }
    }

    private async Task LoadHistoryTablesAsync(HmiWidgetDefinition widget, RuntimeWidgetBinding binding)
    {
        if (binding.HistoryDatabaseCombo?.SelectedItem is not string databaseName || binding.HistoryTableCombo is null || binding.HistoryStatus is null)
        {
            return;
        }
        if (!HasWidgetAccess(widget))
        {
            binding.HistoryStatus.Text = $"Accesso richiesto: livello {widget.RequiredAccessLevel}";
            return;
        }
        try
        {
            var requestedDatabase = databaseName;
            var requestVersion = ++binding.HistoryTableRequestVersion;
            var tables = await new MySqlDatabaseService().GetTablesAsync(_project.Database, databaseName);
            if (requestVersion != binding.HistoryTableRequestVersion || !HasWidgetAccess(widget) ||
                binding.HistoryDatabaseCombo.SelectedItem is not string selectedDatabase ||
                !selectedDatabase.Equals(requestedDatabase, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            binding.HistoryTableCombo.ItemsSource = tables;
            var preferred = string.IsNullOrWhiteSpace(widget.HistoryTableName) ? _project.Database.TableName : widget.HistoryTableName;
            binding.HistoryTableCombo.SelectedItem = tables.FirstOrDefault(name => name.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? tables.FirstOrDefault();
            binding.HistoryStatus.Text = $"{tables.Count} tabelle disponibili in {databaseName}";
            if (binding.HistoryTableCombo.SelectedItem is not null)
            {
                await QueryHistoryAsync(widget, binding);
            }
        }
        catch (Exception ex)
        {
            binding.HistoryStatus.Text = "Tabelle non disponibili: " + ex.Message;
        }
    }

    private async Task QueryHistoryAsync(HmiWidgetDefinition widget, RuntimeWidgetBinding binding)
    {
        if (binding.HistoryDatabaseCombo?.SelectedItem is not string databaseName ||
            binding.HistoryTableCombo?.SelectedItem is not string tableName ||
            binding.HistoryGrid is null || binding.HistoryStatus is null)
        {
            return;
        }
        if (!HasWidgetAccess(widget))
        {
            binding.HistoryGrid.ItemsSource = null;
            binding.HistoryStatus.Text = $"Accesso richiesto: livello {widget.RequiredAccessLevel}";
            return;
        }
        try
        {
            binding.HistoryStatus.Text = "Lettura dati storici…";
            var fromUtc = CombineLocalDateAndTime(binding.HistoryFromPicker?.SelectedDate, binding.HistoryFromTimeBox?.Text, false) is DateTime fromDate
                ? DateTime.SpecifyKind(fromDate, DateTimeKind.Local).ToUniversalTime()
                : (DateTime?)null;
            var toUtc = CombineLocalDateAndTime(binding.HistoryToPicker?.SelectedDate, binding.HistoryToTimeBox?.Text, true) is DateTime toDate
                ? DateTime.SpecifyKind(toDate, DateTimeKind.Local).ToUniversalTime()
                : (DateTime?)null;
            if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            {
                binding.HistoryStatus.Text = "Intervallo non valido: la data iniziale deve precedere quella finale";
                return;
            }
            var options = new HistoryQueryOptions(
                fromUtc,
                toUtc,
                binding.HistorySearchBox?.Text ?? string.Empty,
                Math.Clamp(ParseInt(binding.HistoryLimitBox?.Text ?? string.Empty, widget.HistoryMaxRows), 1, 10000));
            var requestVersion = ++binding.HistoryQueryRequestVersion;
            var table = await new MySqlDatabaseService().QueryTableAsync(_project.Database, databaseName, tableName, options);
            if (requestVersion != binding.HistoryQueryRequestVersion || !HasWidgetAccess(widget) ||
                binding.HistoryDatabaseCombo.SelectedItem is not string currentDatabase ||
                binding.HistoryTableCombo.SelectedItem is not string currentTable ||
                !currentDatabase.Equals(databaseName, StringComparison.OrdinalIgnoreCase) ||
                !currentTable.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            binding.HistoryGrid.ItemsSource = table.DefaultView;
            binding.HistoryStatus.Text = $"{table.Rows.Count} record · {databaseName}.{tableName}";
        }
        catch (Exception ex)
        {
            binding.HistoryStatus.Text = "Query non riuscita: " + ex.Message;
        }
    }

    private async Task LoadHistoricalTrendAsync(HmiWidgetDefinition widget, RuntimeWidgetBinding binding)
    {
        if (binding.ChartStatus is null || binding.ChartSeriesBindings is null)
        {
            return;
        }
        if (!HasWidgetAccess(widget))
        {
            foreach (var series in binding.ChartSeriesBindings)
            {
                series.Points.Clear();
            }
            binding.ChartStatus.Text = $"Accesso richiesto: livello {widget.RequiredAccessLevel}";
            RenderTrendChart(widget, binding);
            return;
        }
        if (binding.ChartSeriesBindings.Count == 0)
        {
            binding.ChartStatus.Text = "Aggiungere almeno una serie al grafico";
            return;
        }
        var databaseName = string.IsNullOrWhiteSpace(widget.HistoryDatabaseName) ? _project.Database.DatabaseName : widget.HistoryDatabaseName;
        var tableName = string.IsNullOrWhiteSpace(widget.HistoryTableName) ? _project.Database.TableName : widget.HistoryTableName;
        binding.ChartStatus.Text = $"Caricamento storico · {binding.ChartSeriesBindings.Count} serie…";
        var requestVersion = ++binding.ChartHistoricalRequestVersion;
        var toUtc = DateTime.UtcNow;
        using var queryLimiter = new SemaphoreSlim(4, 4);
        var tasks = binding.ChartSeriesBindings
            .Select(series => LoadHistoricalTrendSeriesAsync(widget, series, databaseName, tableName, toUtc, queryLimiter))
            .ToList();
        var results = await Task.WhenAll(tasks);
        if (requestVersion != binding.ChartHistoricalRequestVersion || !HasWidgetAccess(widget) ||
            !_runtimeBindings.TryGetValue(widget.Id, out var currentBinding) ||
            !ReferenceEquals(currentBinding, binding))
        {
            return;
        }
        foreach (var result in results)
        {
            result.Series.Points = result.Points;
        }
        RenderTrendChart(widget, binding);
        var failed = results.Count(result => !string.IsNullOrWhiteSpace(result.Error));
        if (failed > 0)
        {
            binding.ChartStatus.Text += $" · {failed} serie non disponibili";
            binding.ChartStatus.ToolTip = string.Join(Environment.NewLine, results.Where(result => !string.IsNullOrWhiteSpace(result.Error)).Select(result => result.Error));
        }
        else
        {
            binding.ChartStatus.ToolTip = null;
        }
    }

    private async Task<HistoricalTrendSeriesResult> LoadHistoricalTrendSeriesAsync(
        HmiWidgetDefinition widget,
        RuntimeChartSeriesBinding series,
        string databaseName,
        string tableName,
        DateTime toUtc,
        SemaphoreSlim queryLimiter)
    {
        var tag = _project.Tags.FirstOrDefault(item => item.Id == series.Definition.TagId);
        if (tag is null)
        {
            return new HistoricalTrendSeriesResult(series, [], $"{series.Definition.DisplayName}: tag non disponibile");
        }
        try
        {
            await queryLimiter.WaitAsync();
            var points = await new MySqlDatabaseService().GetHistoryPointsAsync(
                _project.Database,
                databaseName,
                tableName,
                tag.Id,
                tag.Name,
                toUtc.AddHours(-widget.HistoryHours),
                toUtc,
                widget.HistoryMaxRows);
            return new HistoricalTrendSeriesResult(series, points, null);
        }
        catch (Exception ex)
        {
            return new HistoricalTrendSeriesResult(series, [], $"{series.Definition.DisplayName}: {ex.Message}");
        }
        finally
        {
            queryLimiter.Release();
        }
    }

    private void AddLiveTrendPoint(HmiWidgetDefinition widget, string tagId, object value)
    {
        if (widget.ChartSource != ChartDataSource.LivePlc ||
            !_runtimeBindings.TryGetValue(widget.Id, out var binding) ||
            binding.ChartSeriesBindings is null)
        {
            return;
        }
        var targetSeries = binding.ChartSeriesBindings.Where(series => series.Definition.TagId == tagId).ToList();
        if (targetSeries.Count == 0)
        {
            return;
        }
        if (!TryConvertTrendValue(value, out var numericValue))
        {
            if (binding.ChartStatus is not null)
            {
                binding.ChartStatus.Text = $"Valore non numerico per {string.Join(", ", targetSeries.Select(series => series.Definition.DisplayName))}";
            }
            return;
        }
        var now = DateTime.UtcNow;
        var cutoff = now.AddHours(-widget.HistoryHours);
        foreach (var series in targetSeries)
        {
            series.Points.Add(new HistoryDataPoint(now, numericValue));
            series.Points.RemoveAll(point => point.TimestampUtc < cutoff);
            if (series.Points.Count > widget.HistoryMaxRows)
            {
                series.Points.RemoveRange(0, series.Points.Count - widget.HistoryMaxRows);
            }
        }
        var renderInterval = TimeSpan.FromMilliseconds(150);
        var elapsedSinceRender = now - binding.LastChartRenderUtc;
        if (elapsedSinceRender >= renderInterval)
        {
            binding.LastChartRenderUtc = now;
            RenderTrendChart(widget, binding);
        }
        else if (!binding.ChartRenderScheduled)
        {
            binding.ChartRenderScheduled = true;
            _ = ScheduleTrendChartRenderAsync(widget, binding, renderInterval - elapsedSinceRender);
        }
    }

    private async Task ScheduleTrendChartRenderAsync(HmiWidgetDefinition widget, RuntimeWidgetBinding binding, TimeSpan delay)
    {
        await Task.Delay(delay);
        if (!_runtimeMode ||
            !_runtimeBindings.TryGetValue(widget.Id, out var currentBinding) ||
            !ReferenceEquals(currentBinding, binding))
        {
            binding.ChartRenderScheduled = false;
            return;
        }
        binding.ChartRenderScheduled = false;
        binding.LastChartRenderUtc = DateTime.UtcNow;
        RenderTrendChart(widget, binding);
    }

    private static bool TryConvertTrendValue(object value, out double numericValue)
    {
        if (value is bool booleanValue)
        {
            numericValue = booleanValue ? 1d : 0d;
            return true;
        }
        return double.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out numericValue);
    }

    private void RenderTrendChart(HmiWidgetDefinition widget, RuntimeWidgetBinding binding)
    {
        if (binding.ChartCanvas is null || binding.ChartSeriesBindings is null)
        {
            return;
        }
        var width = Math.Max(10, binding.ChartCanvas.ActualWidth);
        var height = Math.Max(10, binding.ChartCanvas.ActualHeight);
        var allPoints = binding.ChartSeriesBindings.SelectMany(series => series.Points).ToList();
        if (allPoints.Count == 0)
        {
            foreach (var series in binding.ChartSeriesBindings)
            {
                series.Line.Points = [];
            }
            SetChartAxisLabels(binding, string.Empty, string.Empty, string.Empty, string.Empty);
            if (binding.ChartStatus is not null)
            {
                binding.ChartStatus.Text = widget.ChartSource == ChartDataSource.LivePlc ? "In attesa di dati PLC" : "Nessun dato storico";
            }
            return;
        }
        var minValue = allPoints.Min(point => point.Value);
        var maxValue = allPoints.Max(point => point.Value);
        if (Math.Abs(maxValue - minValue) < 0.000001)
        {
            minValue -= 1;
            maxValue += 1;
        }
        var minTime = allPoints.Min(point => point.TimestampUtc.Ticks);
        var maxTime = allPoints.Max(point => point.TimestampUtc.Ticks);
        var displayMaxTime = maxTime;
        if (maxTime == minTime)
        {
            maxTime = minTime + TimeSpan.TicksPerSecond;
        }
        const double left = 48;
        const double top = 10;
        const double right = 10;
        const double bottom = 24;
        var plotWidth = Math.Max(10, width - left - right);
        var plotHeight = Math.Max(10, height - top - bottom);
        if (binding.ChartGridLines is { Count: >= 10 })
        {
            for (var index = 0; index < 5; index++)
            {
                var horizontalY = top + plotHeight * index / 4d;
                var horizontal = binding.ChartGridLines[index];
                horizontal.X1 = left;
                horizontal.X2 = left + plotWidth;
                horizontal.Y1 = horizontalY;
                horizontal.Y2 = horizontalY;
                var verticalX = left + plotWidth * index / 4d;
                var vertical = binding.ChartGridLines[index + 5];
                vertical.X1 = verticalX;
                vertical.X2 = verticalX;
                vertical.Y1 = top;
                vertical.Y2 = top + plotHeight;
            }
        }
        var renderLimit = Math.Clamp((int)Math.Ceiling(plotWidth * 2), 100, 2000);
        foreach (var series in binding.ChartSeriesBindings)
        {
            IReadOnlyList<HistoryDataPoint> renderedPoints = series.Points;
            if (series.Points.Count > renderLimit)
            {
                renderedPoints = Enumerable.Range(0, renderLimit)
                    .Select(index => series.Points[(int)Math.Round(index * (series.Points.Count - 1d) / (renderLimit - 1d))])
                    .ToList();
            }
            series.Line.Points = new PointCollection(renderedPoints.Select(point => new Point(
                left + (point.TimestampUtc.Ticks - minTime) / (double)(maxTime - minTime) * plotWidth,
                top + plotHeight - (point.Value - minValue) / (maxValue - minValue) * plotHeight)));
        }
        SetChartAxisLabels(
            binding,
            maxValue.ToString("0.###", CultureInfo.CurrentCulture),
            minValue.ToString("0.###", CultureInfo.CurrentCulture),
            new DateTime(minTime, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture),
            new DateTime(displayMaxTime, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture));
        if (binding.ChartMaxLabel is not null)
        {
            Canvas.SetLeft(binding.ChartMaxLabel, 4);
            Canvas.SetTop(binding.ChartMaxLabel, top - 6);
        }
        if (binding.ChartMinLabel is not null)
        {
            Canvas.SetLeft(binding.ChartMinLabel, 4);
            Canvas.SetTop(binding.ChartMinLabel, top + plotHeight - 10);
        }
        if (binding.ChartStartTimeLabel is not null)
        {
            Canvas.SetLeft(binding.ChartStartTimeLabel, left);
            Canvas.SetTop(binding.ChartStartTimeLabel, top + plotHeight + 4);
        }
        if (binding.ChartEndTimeLabel is not null)
        {
            Canvas.SetLeft(binding.ChartEndTimeLabel, Math.Max(left, left + plotWidth - binding.ChartEndTimeLabel.Width));
            Canvas.SetTop(binding.ChartEndTimeLabel, top + plotHeight + 4);
        }
        if (binding.ChartStatus is not null)
        {
            var activeSeries = binding.ChartSeriesBindings.Count(series => series.Points.Count > 0);
            binding.ChartStatus.Text = $"{activeSeries}/{binding.ChartSeriesBindings.Count} serie · {allPoints.Count} punti · min {minValue:0.###} · max {maxValue:0.###}";
        }
    }

    private static TextBlock ChartAxisLabel() => new()
    {
        Foreground = BrushOf("#718397"),
        FontSize = 9,
        IsHitTestVisible = false
    };

    private static void SetChartAxisLabels(RuntimeWidgetBinding binding, string maximum, string minimum, string startTime, string endTime)
    {
        if (binding.ChartMaxLabel is not null)
        {
            binding.ChartMaxLabel.Text = maximum;
        }
        if (binding.ChartMinLabel is not null)
        {
            binding.ChartMinLabel.Text = minimum;
        }
        if (binding.ChartStartTimeLabel is not null)
        {
            binding.ChartStartTimeLabel.Text = startTime;
        }
        if (binding.ChartEndTimeLabel is not null)
        {
            binding.ChartEndTimeLabel.Text = endTime;
        }
    }

    private static FrameworkElement RuntimeFilterField(string label, FrameworkElement editor)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            Foreground = BrushOf("#8FA0B3"),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(1, 0, 0, 3)
        });
        panel.Children.Add(editor);
        return panel;
    }

    private Button RuntimeActionButton(string text, string background)
    {
        var button = new Button
        {
            Content = text,
            Background = BrushOf(background),
            Foreground = BrushOf("#F8FAFC"),
            BorderBrush = BrushOf("#3A4B5D"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 6, 11, 6),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };
        return button;
    }

    private void RefreshRecipeBinding(RuntimeWidgetBinding binding)
    {
        if (binding.RecipeValuesPanel is null)
        {
            return;
        }
        if (binding.RecipeValuesPanel.IsKeyboardFocusWithin)
        {
            return;
        }
        binding.RecipeValuesPanel.Children.Clear();
        if (binding.RecipeBook is null)
        {
            binding.RecipeValuesPanel.Children.Add(new TextBlock { Text = "Nessun ricettario collegato", Foreground = BrushOf("#8FA0B3") });
            return;
        }
        var recipe = binding.RecipeCombo?.SelectedItem as RecipeSetDefinition;
        foreach (var tagId in binding.RecipeBook.TagIds)
        {
            var tag = _project.Tags.FirstOrDefault(item => item.Id == tagId);
            if (tag is null)
            {
                continue;
            }
            var storedValue = recipe?.Values.GetValueOrDefault(tagId) ?? "0";
            var liveValue = _runtime.GetValue(tagId);
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.Children.Add(new TextBlock { Text = tag.Name, Foreground = BrushOf("#C9D4E0"), VerticalAlignment = VerticalAlignment.Center });
            var valueEditor = new TextBox
            {
                Text = storedValue,
                Tag = tagId,
                Margin = new Thickness(7, 1, 7, 1),
                Padding = new Thickness(6, 3, 6, 3),
                ToolTip = "Valore della ricetta modificabile"
            };
            valueEditor.LostKeyboardFocus += async (_, _) =>
            {
                if (EnsureBindingAccess(binding, "modifica ricetta") &&
                    binding.RecipeCombo?.SelectedItem is RecipeSetDefinition selectedRecipe && valueEditor.Tag is string selectedTagId)
                {
                    selectedRecipe.Values[selectedTagId] = valueEditor.Text.Trim();
                    await CommitRecipeChangeAsync();
                }
            };
            Grid.SetColumn(valueEditor, 1);
            row.Children.Add(valueEditor);
            var live = new TextBlock
            {
                Text = "PLC  " + FormatTagValue(liveValue ?? "—", 2),
                Foreground = BrushOf("#28C2B8"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(live, 2);
            row.Children.Add(live);
            binding.RecipeValuesPanel.Children.Add(row);
        }
    }

    private void RefreshRuntimeUserManagers()
    {
        foreach (var binding in _runtimeBindings.Values.Where(binding => binding.UserList is not null))
        {
            RefreshRuntimeUserManager(binding);
        }
    }

    private void RefreshRuntimeUserManager(RuntimeWidgetBinding binding)
    {
        if (binding.UserList is null)
        {
            return;
        }
        var selectedId = (binding.UserList.SelectedItem as UserDefinition)?.Id;
        var users = _project.Security.Users
            .OrderByDescending(user => user.AccessLevel)
            .ThenBy(user => user.Username)
            .ToList();
        binding.UserList.ItemsSource = null;
        binding.UserList.ItemsSource = users;
        binding.UserList.SelectedItem = users.FirstOrDefault(user => user.Id == selectedId) ?? users.FirstOrDefault();
        binding.UserSessionStatus!.Text = _project.Security.Enabled
            ? _currentUser is null
                ? "Nessun utente autenticato"
                : $"{_currentUser.DisplayName} · livello {_currentUser.AccessLevel}"
            : "Sicurezza non abilitata";
        if (binding.UserAuditGrid is not null)
        {
            binding.UserAuditGrid.ItemsSource = _userSessionAudit.GetEntries()
                .Take(1000)
                .Select(entry => new UserSessionRuntimeRow(
                    string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Username : entry.DisplayName,
                    entry.AccessLevel,
                    entry.LoggedInAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
                    entry.LoggedOutAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "Sessione aperta",
                    UserSessionEndReasonLabel(entry.EndReason)))
                .ToList();
        }
    }

    private void PopulateRuntimeUserEditor(RuntimeWidgetBinding binding, UserDefinition? user)
    {
        if (binding.UserUsernameBox is null || binding.UserDisplayNameBox is null || binding.UserAccessLevelBox is null ||
            binding.UserActiveCheck is null || binding.UserPasswordBox is null || binding.UserPasswordConfirmBox is null)
        {
            return;
        }
        binding.UserUsernameBox.Text = user?.Username ?? string.Empty;
        binding.UserDisplayNameBox.Text = user?.DisplayName ?? string.Empty;
        binding.UserAccessLevelBox.Text = (user?.AccessLevel ?? Math.Min(10, _currentUser?.AccessLevel ?? 10)).ToString(CultureInfo.InvariantCulture);
        binding.UserActiveCheck.IsChecked = user?.IsActive ?? true;
        binding.UserPasswordBox.Clear();
        binding.UserPasswordConfirmBox.Clear();
        if (user is null)
        {
            binding.UserList!.SelectedItem = null;
            binding.UserUsernameBox.Focus();
        }
    }

    private async Task SaveRuntimeUserAsync(HmiWidgetDefinition widget, RuntimeWidgetBinding binding)
    {
        if (!EnsureRuntimeUserManagementAccess(widget) || binding.UserList is null || binding.UserUsernameBox is null ||
            binding.UserDisplayNameBox is null || binding.UserAccessLevelBox is null || binding.UserActiveCheck is null ||
            binding.UserPasswordBox is null || binding.UserPasswordConfirmBox is null)
        {
            return;
        }
        var selectedUser = binding.UserList.SelectedItem as UserDefinition;
        var password = binding.UserPasswordBox.Password;
        if (!string.Equals(password, binding.UserPasswordConfirmBox.Password, StringComparison.Ordinal))
        {
            MessageBox.Show("Le password inserite non coincidono.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selectedUser is null && string.IsNullOrEmpty(password))
        {
            MessageBox.Show("Per un nuovo utente è obbligatorio impostare una password.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!string.IsNullOrEmpty(password))
        {
            try
            {
                PasswordHashingService.ValidatePassword(password, _project.Security.MinimumPasswordLength);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        var currentLevel = _currentUser!.AccessLevel;
        var requestedLevel = Math.Clamp(ParseInt(binding.UserAccessLevelBox.Text, 0), 0, _project.Security.MaximumAccessLevel);
        if (requestedLevel > currentLevel || selectedUser?.AccessLevel > currentLevel)
        {
            MessageBox.Show("Non puoi creare o modificare un utente con livello superiore al tuo.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (selectedUser is null && requestedLevel >= currentLevel && currentLevel < _project.Security.MaximumAccessLevel)
        {
            MessageBox.Show("Puoi creare soltanto utenti con livello inferiore al tuo.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (selectedUser is not null && selectedUser.Id != _currentUser.Id &&
            (selectedUser.AccessLevel >= currentLevel || requestedLevel >= currentLevel))
        {
            MessageBox.Show("Puoi modificare altri utenti soltanto se il loro livello, incluso quello richiesto, è inferiore al tuo.",
                "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var isActive = binding.UserActiveCheck.IsChecked == true;
        if (selectedUser?.Id == _currentUser.Id && !isActive)
        {
            MessageBox.Show("Non puoi disattivare l'utente attualmente connesso.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (selectedUser is not null && IsConfiguredAdministrator(selectedUser) &&
            (!isActive || requestedLevel < _project.Security.MaximumAccessLevel) &&
            _project.Security.Users.Count(IsConfiguredAdministrator) <= 1)
        {
            MessageBox.Show("Non è possibile disattivare o declassare l'ultimo amministratore attivo.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var savedUser = selectedUser is null
                ? _userSecurity.CreateUser(_project.Security, binding.UserUsernameBox.Text, binding.UserDisplayNameBox.Text, requestedLevel, isActive, password)
                : _userSecurity.UpdateUser(_project.Security, selectedUser.Id, binding.UserUsernameBox.Text, binding.UserDisplayNameBox.Text, requestedLevel, isActive);
            if (selectedUser is not null && !string.IsNullOrEmpty(password))
            {
                _userSecurity.ChangePassword(_project.Security, savedUser.Id, password);
            }
            if (_currentUser.Id == savedUser.Id)
            {
                _currentUser = new AuthenticatedUserIdentity(savedUser.Id, savedUser.Username, savedUser.DisplayName, savedUser.AccessLevel);
            }
            await CommitRuntimeProjectChangeAsync("Modifiche utenti salvate", persistSecurityOverride: true);
            RefreshRuntimeAccessStates();
            RefreshRuntimeUserManagers();
            binding.UserList.SelectedItem = _project.Security.Users.FirstOrDefault(user => user.Id == savedUser.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task DeleteRuntimeUserAsync(HmiWidgetDefinition widget, RuntimeWidgetBinding binding)
    {
        if (!EnsureRuntimeUserManagementAccess(widget) || binding.UserList?.SelectedItem is not UserDefinition user)
        {
            return;
        }
        if (_currentUser?.Id == user.Id)
        {
            MessageBox.Show("Non puoi eliminare l'utente attualmente connesso.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (user.AccessLevel >= _currentUser!.AccessLevel)
        {
            MessageBox.Show("Puoi eliminare soltanto utenti con livello inferiore al tuo.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (IsConfiguredAdministrator(user) && _project.Security.Users.Count(IsConfiguredAdministrator) <= 1)
        {
            MessageBox.Show("Non è possibile eliminare l'ultimo amministratore attivo.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"Eliminare l'utente '{user.Username}'?", "Gestione utenti", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        if (!EnsureRuntimeUserManagementAccess(widget) || _currentUser is null || user.AccessLevel >= _currentUser.AccessLevel)
        {
            return;
        }
        _userSecurity.DeleteUser(_project.Security, user.Id);
        await CommitRuntimeProjectChangeAsync("Utente eliminato", persistSecurityOverride: true);
        RefreshRuntimeUserManagers();
    }

    private bool EnsureRuntimeUserManagementAccess(HmiWidgetDefinition widget)
    {
        if (!_project.Security.Enabled)
        {
            MessageBox.Show("Abilitare la sicurezza e configurare il primo amministratore nello sviluppo del progetto.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (_currentUser is null)
        {
            MessageBox.Show("Effettuare il login prima di modificare gli utenti.", "Accesso richiesto", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return EnsureWidgetAccess(widget, "gestione utenti");
    }

    private bool EnsureBindingAccess(RuntimeWidgetBinding binding, string operation) =>
        binding.Widget is null || EnsureWidgetAccess(binding.Widget, operation);

    private async Task LoadSelectedRecipeAsync(RuntimeWidgetBinding binding)
    {
        if (!EnsureBindingAccess(binding, "scrittura ricetta sul PLC") ||
            binding.RecipeCombo?.SelectedItem is not RecipeSetDefinition recipe || binding.RecipeBook is null)
        {
            return;
        }
        var allWritten = true;
        foreach (var tagId in binding.RecipeBook.TagIds)
        {
            if (recipe.Values.TryGetValue(tagId, out var value))
            {
                allWritten &= await _runtime.WriteAsync(tagId, value);
            }
        }
        StatusText.Text = allWritten ? $"Ricetta '{recipe.Name}' caricata" : $"Ricetta '{recipe.Name}' caricata parzialmente";
        RefreshRecipeBinding(binding);
    }

    private async Task CaptureSelectedRecipeAsync(RuntimeWidgetBinding binding)
    {
        if (!EnsureBindingAccess(binding, "lettura ricetta dal PLC") ||
            binding.RecipeCombo?.SelectedItem is not RecipeSetDefinition recipe || binding.RecipeBook is null)
        {
            return;
        }
        foreach (var tagId in binding.RecipeBook.TagIds)
        {
            var value = _runtime.GetValue(tagId);
            if (value is not null)
            {
                recipe.Values[tagId] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
        await CommitRecipeChangeAsync();
        StatusText.Text = $"Valori acquisiti nella ricetta '{recipe.Name}'";
        RefreshRecipeBinding(binding);
    }

    private async Task AddRuntimeRecipeAsync(RuntimeWidgetBinding binding)
    {
        if (!EnsureBindingAccess(binding, "creazione ricetta") || binding.RecipeBook is null)
        {
            return;
        }
        var name = TextPromptWindow.Ask(this, "Nuova ricetta", "Nome della ricetta", $"Ricetta {binding.RecipeBook.Recipes.Count + 1}");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        if (!EnsureBindingAccess(binding, "creazione ricetta"))
        {
            return;
        }
        var recipe = new RecipeSetDefinition { Name = name.Trim() };
        foreach (var tagId in binding.RecipeBook.TagIds)
        {
            recipe.Values[tagId] = Convert.ToString(_runtime.GetValue(tagId) ?? 0, CultureInfo.InvariantCulture) ?? "0";
        }
        binding.RecipeBook.Recipes.Add(recipe);
        binding.RecipeCombo!.ItemsSource = null;
        binding.RecipeCombo.ItemsSource = binding.RecipeBook.Recipes;
        binding.RecipeCombo.SelectedItem = recipe;
        await CommitRecipeChangeAsync();
    }

    private async Task DeleteRuntimeRecipeAsync(RuntimeWidgetBinding binding)
    {
        if (!EnsureBindingAccess(binding, "eliminazione ricetta") || binding.RecipeBook is null || binding.RecipeCombo?.SelectedItem is not RecipeSetDefinition recipe)
        {
            return;
        }
        if (MessageBox.Show($"Eliminare la ricetta '{recipe.Name}'?", "Ricette", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes ||
            !EnsureBindingAccess(binding, "eliminazione ricetta"))
        {
            return;
        }
        binding.RecipeBook.Recipes.Remove(recipe);
        binding.RecipeCombo.ItemsSource = null;
        binding.RecipeCombo.ItemsSource = binding.RecipeBook.Recipes;
        binding.RecipeCombo.SelectedIndex = binding.RecipeBook.Recipes.Count > 0 ? 0 : -1;
        await CommitRecipeChangeAsync();
    }

    private async Task RenameRuntimeRecipeAsync(RuntimeWidgetBinding binding)
    {
        if (!EnsureBindingAccess(binding, "rinomina ricetta") || binding.RecipeCombo?.SelectedItem is not RecipeSetDefinition recipe)
        {
            return;
        }
        var name = TextPromptWindow.Ask(this, "Rinomina ricetta", "Nuovo nome", recipe.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        if (!EnsureBindingAccess(binding, "rinomina ricetta"))
        {
            return;
        }
        recipe.Name = name.Trim();
        binding.RecipeCombo.Items.Refresh();
        await CommitRecipeChangeAsync();
    }

    private Task CommitRecipeChangeAsync() => CommitRuntimeProjectChangeAsync("Modifiche ricetta salvate");

    private async Task CommitRuntimeProjectChangeAsync(
        string successMessage,
        bool persistSecurityOverride = false,
        bool showSuccessStatus = true)
    {
        if (!_runtimeOnly)
        {
            MarkDirty();
            return;
        }
        if (string.IsNullOrWhiteSpace(_projectPath))
        {
            return;
        }

        await _runtimeProjectSaveLock.WaitAsync();
        try
        {
            Exception? projectSaveError = null;
            try
            {
                await _storage.SaveAsync(_project, _projectPath);
            }
            catch (Exception ex)
            {
                projectSaveError = ex;
            }
            if (persistSecurityOverride)
            {
                await _runtimeSecurityStore.SaveAsync(_projectPath, _project.Name, _project.Security);
                if (showSuccessStatus)
                {
                    StatusText.Text = projectSaveError is null
                        ? successMessage + " nel pacchetto runtime"
                        : successMessage + " nel profilo locale del pannello";
                }
            }
            else if (projectSaveError is not null)
            {
                var message = $"Modifica applicata solo per la sessione corrente e non salvata su disco: {projectSaveError.Message}";
                StatusText.Text = message;
                MessageBox.Show(message, "Salvataggio runtime non riuscito", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (showSuccessStatus)
            {
                StatusText.Text = successMessage + " nel pacchetto runtime";
            }
        }
        catch (Exception ex)
        {
            var message = $"Modifica applicata solo per la sessione corrente e non salvata su disco: {ex.Message}";
            StatusText.Text = message;
            MessageBox.Show(message, "Salvataggio runtime non riuscito", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _runtimeProjectSaveLock.Release();
        }
    }

    private static Border CardBorder(Brush background, UIElement child) => new()
    {
        Background = background,
        BorderBrush = BrushOf("#334354"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(16),
        Child = child
    };

    private static Border NumericCardBorder(HmiWidgetDefinition widget, Brush background, UIElement child)
    {
        var padding = widget.ShowBackground
            ? Math.Clamp(Math.Min(widget.Width, widget.Height) * 0.06, 1, 12)
            : 0;
        return new Border
        {
            Background = widget.ShowBackground ? background : Brushes.Transparent,
            BorderBrush = widget.ShowBackground ? BrushOf("#334354") : Brushes.Transparent,
            BorderThickness = widget.ShowBackground ? new Thickness(1) : new Thickness(0),
            CornerRadius = widget.ShowBackground ? new CornerRadius(6) : new CornerRadius(0),
            Padding = new Thickness(padding),
            Child = child
        };
    }

    private static Viewbox CreateFittedTextView(TextBlock text, TextAlignment alignment) => new()
    {
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = ToHorizontalAlignment(alignment),
        VerticalAlignment = VerticalAlignment.Center,
        Child = text
    };

    private static void AttachNumericInputAutoFit(TextBox input, double requestedFontSize)
    {
        void Fit() => FitNumericInputText(input, requestedFontSize);
        input.Loaded += (_, _) => Fit();
        input.SizeChanged += (_, _) => Fit();
        input.TextChanged += (_, _) => Fit();
    }

    private static void FitNumericInputText(TextBox input, double requestedFontSize)
    {
        var availableWidth = input.ActualWidth - input.Padding.Left - input.Padding.Right -
            input.BorderThickness.Left - input.BorderThickness.Right - 3;
        var availableHeight = input.ActualHeight - input.Padding.Top - input.Padding.Bottom -
            input.BorderThickness.Top - input.BorderThickness.Bottom - 3;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        var value = string.IsNullOrEmpty(input.Text) ? "0" : input.Text;
        var dpi = VisualTreeHelper.GetDpi(input);
        var formatted = new FormattedText(
            value,
            CultureInfo.CurrentCulture,
            input.FlowDirection,
            new Typeface(input.FontFamily, input.FontStyle, input.FontWeight, input.FontStretch),
            requestedFontSize,
            Brushes.Black,
            dpi.PixelsPerDip);
        var scale = Math.Min(1,
            Math.Min(
                availableWidth / Math.Max(1, formatted.WidthIncludingTrailingWhitespace),
                availableHeight / Math.Max(1, formatted.Height)));
        var fittedSize = Math.Max(1, requestedFontSize * scale * 0.94);
        if (Math.Abs(input.FontSize - fittedSize) > 0.05)
        {
            input.FontSize = fittedSize;
        }
    }

    private void ToggleLeftSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeMode)
        {
            return;
        }
        _leftSidebarCollapsed = !_leftSidebarCollapsed;
        ApplyLeftSidebarLayout();
    }

    private void ToggleRightSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeMode)
        {
            return;
        }
        _rightSidebarCollapsed = !_rightSidebarCollapsed;
        ApplyRightSidebarLayout();
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_runtimeMode)
        {
            return;
        }
        if (sender == LeftSidebarSplitter && !_leftSidebarCollapsed && LeftSidebarColumn.ActualWidth > 0)
        {
            _leftSidebarExpandedWidth = Math.Clamp(LeftSidebarColumn.ActualWidth, LeftSidebarMinimumWidth, LeftSidebarMaximumWidth);
        }
        else if (sender == RightSidebarSplitter && !_rightSidebarCollapsed && RightSidebarColumn.ActualWidth > 0)
        {
            _rightSidebarExpandedWidth = Math.Clamp(RightSidebarColumn.ActualWidth, RightSidebarMinimumWidth, RightSidebarMaximumWidth);
        }
        ConstrainSidebarWidths();
    }

    private void ApplyLeftSidebarLayout(bool focusToggle = true)
    {
        if (_leftSidebarCollapsed)
        {
            if (LeftSidebarColumn.ActualWidth >= LeftSidebarMinimumWidth)
            {
                _leftSidebarExpandedWidth = Math.Clamp(LeftSidebarColumn.ActualWidth, LeftSidebarMinimumWidth, LeftSidebarMaximumWidth);
            }
            DesignerSidebar.Visibility = Visibility.Collapsed;
            LeftSidebarColumn.MinWidth = 0;
            LeftSidebarColumn.MaxWidth = 0;
            LeftSidebarColumn.Width = new GridLength(0);
            LeftSidebarSplitter.IsEnabled = false;
            ToggleLeftSidebarButton.Content = "›";
            // Testi aggiornati per il lato sinistro
            ToggleLeftSidebarButton.ToolTip = "Mostra pannello Pagine e oggetti";
            AutomationProperties.SetName(ToggleLeftSidebarButton, "Mostra pannello Pagine e oggetti");
        }
        else
        {
            LeftSidebarColumn.MaxWidth = LeftSidebarMaximumWidth;
            LeftSidebarColumn.MinWidth = LeftSidebarMinimumWidth;
            LeftSidebarColumn.Width = new GridLength(Math.Clamp(_leftSidebarExpandedWidth, LeftSidebarMinimumWidth, LeftSidebarMaximumWidth));
            DesignerSidebar.Visibility = Visibility.Visible;
            LeftSidebarSplitter.IsEnabled = true;
            ToggleLeftSidebarButton.Content = "‹";
            // Testi aggiornati per il lato sinistro
            ToggleLeftSidebarButton.ToolTip = "Nascondi pannello Pagine e oggetti";
            AutomationProperties.SetName(ToggleLeftSidebarButton, "Nascondi pannello Pagine e oggetti");
        }
        ConstrainSidebarWidths();
        if (focusToggle)
        {
            ToggleLeftSidebarButton.Focus();
        }
    }

    private void ApplyRightSidebarLayout(bool focusToggle = true)
    {
        if (_rightSidebarCollapsed)
        {
            if (RightSidebarColumn.ActualWidth >= RightSidebarMinimumWidth)
            {
                _rightSidebarExpandedWidth = Math.Clamp(RightSidebarColumn.ActualWidth, RightSidebarMinimumWidth, RightSidebarMaximumWidth);
            }
            InspectorSidebar.Visibility = Visibility.Collapsed;
            RightSidebarColumn.MinWidth = 0;
            RightSidebarColumn.MaxWidth = 0;
            RightSidebarColumn.Width = new GridLength(0);
            RightSidebarSplitter.IsEnabled = false;
            ToggleRightSidebarButton.Content = "‹";
            // Testi aggiornati per il lato destro
            ToggleRightSidebarButton.ToolTip = "Mostra pannello Proprietà";
            AutomationProperties.SetName(ToggleRightSidebarButton, "Mostra pannello Proprietà");
        }
        else
        {
            RightSidebarColumn.MaxWidth = RightSidebarMaximumWidth;
            RightSidebarColumn.MinWidth = RightSidebarMinimumWidth;
            RightSidebarColumn.Width = new GridLength(Math.Clamp(_rightSidebarExpandedWidth, RightSidebarMinimumWidth, RightSidebarMaximumWidth));
            InspectorSidebar.Visibility = Visibility.Visible;
            RightSidebarSplitter.IsEnabled = true;
            ToggleRightSidebarButton.Content = "›";
            // Testi aggiornati per il lato destro
            ToggleRightSidebarButton.ToolTip = "Nascondi pannello Proprietà";
            AutomationProperties.SetName(ToggleRightSidebarButton, "Nascondi pannello Proprietà");
        }
        ConstrainSidebarWidths();
        if (focusToggle)
        {
            ToggleRightSidebarButton.Focus();
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRuntimeViewport();
        ConstrainSidebarWidths();
    }

    private void ConstrainSidebarWidths()
    {
        if (_runtimeMode || MainWorkspace.ActualWidth <= 0 || LeftSidebarRail.Visibility != Visibility.Visible ||
            RightSidebarRail.Visibility != Visibility.Visible)
        {
            return;
        }

        var leftWidth = _leftSidebarCollapsed
            ? 0
            : Math.Clamp(_leftSidebarExpandedWidth, LeftSidebarMinimumWidth, LeftSidebarMaximumWidth);
        var rightWidth = _rightSidebarCollapsed
            ? 0
            : Math.Clamp(_rightSidebarExpandedWidth, RightSidebarMinimumWidth, RightSidebarMaximumWidth);
        var availableWidth = Math.Max(0, MainWorkspace.ActualWidth - (SidebarRailWidth * 2) - WorkspaceMinimumWidth);
        var overflow = leftWidth + rightWidth - availableWidth;
        if (overflow > 0)
        {
            var leftMinimum = _leftSidebarCollapsed ? 0 : LeftSidebarMinimumWidth;
            var rightMinimum = _rightSidebarCollapsed ? 0 : RightSidebarMinimumWidth;
            var leftSurplus = Math.Max(0, leftWidth - leftMinimum);
            var rightSurplus = Math.Max(0, rightWidth - rightMinimum);
            var totalSurplus = leftSurplus + rightSurplus;
            if (totalSurplus > 0)
            {
                var leftReduction = Math.Min(leftSurplus, overflow * leftSurplus / totalSurplus);
                leftWidth -= leftReduction;
                overflow -= leftReduction;
                rightWidth -= Math.Min(rightSurplus, overflow);
            }
        }

        if (!_leftSidebarCollapsed)
        {
            LeftSidebarColumn.Width = new GridLength(Math.Max(LeftSidebarMinimumWidth, leftWidth));
        }
        if (!_rightSidebarCollapsed)
        {
            RightSidebarColumn.Width = new GridLength(Math.Max(RightSidebarMinimumWidth, rightWidth));
        }
    }

    private void HideEditorSidebarsForRuntime()
    {
        if (!_leftSidebarCollapsed && LeftSidebarColumn.ActualWidth >= LeftSidebarMinimumWidth)
        {
            _leftSidebarExpandedWidth = Math.Clamp(LeftSidebarColumn.ActualWidth, LeftSidebarMinimumWidth, LeftSidebarMaximumWidth);
        }
        if (!_rightSidebarCollapsed && RightSidebarColumn.ActualWidth >= RightSidebarMinimumWidth)
        {
            _rightSidebarExpandedWidth = Math.Clamp(RightSidebarColumn.ActualWidth, RightSidebarMinimumWidth, RightSidebarMaximumWidth);
        }
        DesignerSidebar.Visibility = Visibility.Collapsed;
        InspectorSidebar.Visibility = Visibility.Collapsed;
        LeftSidebarRail.Visibility = Visibility.Collapsed;
        RightSidebarRail.Visibility = Visibility.Collapsed;
        LeftSidebarColumn.MinWidth = 0;
        LeftSidebarColumn.MaxWidth = 0;
        LeftSidebarColumn.Width = new GridLength(0);
        RightSidebarColumn.MinWidth = 0;
        RightSidebarColumn.MaxWidth = 0;
        RightSidebarColumn.Width = new GridLength(0);
        LeftSidebarRailColumn.Width = new GridLength(0);
        RightSidebarRailColumn.Width = new GridLength(0);
    }

    private void RestoreEditorSidebarsAfterRuntime()
    {
        LeftSidebarRailColumn.Width = new GridLength(28);
        RightSidebarRailColumn.Width = new GridLength(28);
        LeftSidebarRail.Visibility = Visibility.Visible;
        RightSidebarRail.Visibility = Visibility.Visible;
        ApplyLeftSidebarLayout(focusToggle: false);
        ApplyRightSidebarLayout(focusToggle: false);
    }

    private void DesignerItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid root || root.Tag is not HmiWidgetDefinition widget)
        {
            return;
        }

        CommitAnimationRuleEditor();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_selectedWidgetIds.Add(widget.Id))
            {
                _selectedWidgetIds.Remove(widget.Id);
                if (_selectedWidget?.Id == widget.Id)
                {
                    _selectedWidget = GetSelectedWidgets().LastOrDefault();
                }
            }
            else
            {
                _selectedWidget = widget;
            }
            _pageFolderInspectorActive = false;
            UpdateDesignerSelection();
            ShowWidgetInspector();
            e.Handled = true;
            return;
        }

        if (!_selectedWidgetIds.Contains(widget.Id))
        {
            SelectOnlyWidget(widget);
        }
        else
        {
            _selectedWidget = widget;
        }
        _pageFolderInspectorActive = false;
        _dragging = true;
        _dragStart = e.GetPosition(DesignCanvas);
        _dragOrigins.Clear();
        foreach (var selected in GetSelectedWidgets())
        {
            _dragOrigins[selected.Id] = new Point(selected.X, selected.Y);
        }
        root.CaptureMouse();
        UpdateDesignerSelection();
        ShowWidgetInspector();
        e.Handled = true;
    }

    private void DesignerItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || sender is not Grid root || root.Tag is not HmiWidgetDefinition widget || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var selectedWidgets = GetSelectedWidgets();
        if (selectedWidgets.Count == 0 || _selectedPage is null)
        {
            return;
        }
        var position = e.GetPosition(DesignCanvas);
        var deltaX = Snap(position.X - _dragStart.X);
        var deltaY = Snap(position.Y - _dragStart.Y);
        var minimumDeltaX = selectedWidgets.Max(selected => -_dragOrigins[selected.Id].X);
        var maximumDeltaX = selectedWidgets.Min(selected => _selectedPage.Width - (_dragOrigins[selected.Id].X + selected.Width));
        var minimumDeltaY = selectedWidgets.Max(selected => -_dragOrigins[selected.Id].Y);
        var maximumDeltaY = selectedWidgets.Min(selected => _selectedPage.Height - (_dragOrigins[selected.Id].Y + selected.Height));
        deltaX = Clamp(deltaX, minimumDeltaX, maximumDeltaX);
        deltaY = Clamp(deltaY, minimumDeltaY, maximumDeltaY);
        foreach (var selected in selectedWidgets)
        {
            selected.X = _dragOrigins[selected.Id].X + deltaX;
            selected.Y = _dragOrigins[selected.Id].Y + deltaY;
        }
        foreach (var selectedRoot in DesignCanvas.Children.OfType<Grid>().Where(item => item.Tag is HmiWidgetDefinition selected && _selectedWidgetIds.Contains(selected.Id)))
        {
            var selected = (HmiWidgetDefinition)selectedRoot.Tag;
            Canvas.SetLeft(selectedRoot, selected.X);
            Canvas.SetTop(selectedRoot, selected.Y);
        }
        UpdatePositionFields();
    }

    private void DesignerItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || sender is not Grid root)
        {
            return;
        }

        _dragging = false;
        root.ReleaseMouseCapture();
        MarkDirty();
        e.Handled = true;
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb grip || grip.Tag is not HmiWidgetDefinition widget)
        {
            return;
        }

        widget.Width = Clamp(Snap(widget.Width + e.HorizontalChange), 70, (_selectedPage?.Width ?? _project.CanvasWidth) - widget.X);
        widget.Height = Clamp(Snap(widget.Height + e.VerticalChange), 36, (_selectedPage?.Height ?? _project.CanvasHeight) - widget.Y);
        if (grip.Parent is Grid root)
        {
            root.Width = widget.Width;
            root.Height = widget.Height;
        }
        ShowWidgetInspector();
        e.Handled = true;
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        MarkDirty();
        RenderDesigner();
    }

    private void UpdateDesignerSelection()
    {
        foreach (var root in DesignCanvas.Children.OfType<Grid>())
        {
            if (root.Tag is not HmiWidgetDefinition widget || root.Children.Count < 2)
            {
                continue;
            }

            var isPrimary = widget.Id == _selectedWidget?.Id;
            var isSelected = _selectedWidgetIds.Contains(widget.Id);
            if (root.Children[0] is Border border)
            {
                border.BorderBrush = isPrimary ? BrushOf("#28C2B8") : isSelected ? BrushOf("#227CFF") : Brushes.Transparent;
                border.BorderThickness = new Thickness(isSelected ? 2 : 1);
            }
            root.Children[1].Visibility = isPrimary ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateAlignmentCommandState();
    }

    private List<HmiWidgetDefinition> GetSelectedWidgets() => _selectedPage?.Widgets
        .Where(widget => _selectedWidgetIds.Contains(widget.Id))
        .ToList() ?? [];

    private void SelectOnlyWidget(HmiWidgetDefinition widget)
    {
        _selectedWidgetIds.Clear();
        _selectedWidgetIds.Add(widget.Id);
        _selectedWidget = widget;
    }

    private void ClearWidgetSelection()
    {
        _selectedWidgetIds.Clear();
        _selectedWidget = null;
        _dragOrigins.Clear();
        _dragging = false;
    }

    private void UpdateAlignmentCommandState()
    {
        var selectedCount = _selectedWidgetIds.Count;
        var relativeEnabled = !_runtimeMode && selectedCount >= 2;
        foreach (var button in new[] { AlignLeftButton, AlignCenterXButton, AlignRightButton, AlignTopButton, AlignCenterYButton, AlignBottomButton })
        {
            button.IsEnabled = relativeEnabled;
        }
        var pageEnabled = !_runtimeMode && selectedCount >= 1;
        foreach (var button in new[] { CenterPageXButton, CenterPageYButton, CenterPageBothButton })
        {
            button.IsEnabled = pageEnabled;
        }
    }

    private void AlignWidgets_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeMode || _selectedPage is null || sender is not Button { Tag: string operation })
        {
            return;
        }
        ApplyWidgetInspector();
        var selected = GetSelectedWidgets();
        if (selected.Count == 0)
        {
            StatusText.Text = "Selezionare almeno un oggetto";
            return;
        }
        if (operation.StartsWith("Selection", StringComparison.Ordinal) && selected.Count < 2)
        {
            StatusText.Text = "Usare Ctrl+clic per selezionare almeno due oggetti";
            return;
        }

        if (operation.StartsWith("Page", StringComparison.Ordinal))
        {
            var minX = selected.Min(widget => widget.X);
            var minY = selected.Min(widget => widget.Y);
            var maxX = selected.Max(widget => widget.X + widget.Width);
            var maxY = selected.Max(widget => widget.Y + widget.Height);
            var deltaX = (_selectedPage.Width - (maxX - minX)) / 2d - minX;
            var deltaY = (_selectedPage.Height - (maxY - minY)) / 2d - minY;
            foreach (var widget in selected)
            {
                if (operation is "PageCenterX" or "PageCenterBoth")
                {
                    widget.X = Clamp(widget.X + deltaX, 0, _selectedPage.Width - widget.Width);
                }
                if (operation is "PageCenterY" or "PageCenterBoth")
                {
                    widget.Y = Clamp(widget.Y + deltaY, 0, _selectedPage.Height - widget.Height);
                }
            }
        }
        else
        {
            var reference = _selectedWidget ?? selected[0];
            foreach (var widget in selected.Where(widget => widget.Id != reference.Id))
            {
                switch (operation)
                {
                    case "SelectionLeft":
                        widget.X = Clamp(reference.X, 0, _selectedPage.Width - widget.Width);
                        break;
                    case "SelectionCenterX":
                        widget.X = Clamp(reference.X + (reference.Width - widget.Width) / 2d, 0, _selectedPage.Width - widget.Width);
                        break;
                    case "SelectionRight":
                        widget.X = Clamp(reference.X + reference.Width - widget.Width, 0, _selectedPage.Width - widget.Width);
                        break;
                    case "SelectionTop":
                        widget.Y = Clamp(reference.Y, 0, _selectedPage.Height - widget.Height);
                        break;
                    case "SelectionCenterY":
                        widget.Y = Clamp(reference.Y + (reference.Height - widget.Height) / 2d, 0, _selectedPage.Height - widget.Height);
                        break;
                    case "SelectionBottom":
                        widget.Y = Clamp(reference.Y + reference.Height - widget.Height, 0, _selectedPage.Height - widget.Height);
                        break;
                }
            }
        }
        MarkDirty();
        RenderDesigner();
        ShowWidgetInspector();
        StatusText.Text = selected.Count == 1 ? "Oggetto centrato nella pagina" : $"{selected.Count} oggetti allineati";
    }

    private void ShowWidgetInspector()
    {
        _updatingInspector = true;
        var hasSelection = _selectedWidget is not null;
        var hasPageFolderSelection = !hasSelection && _pageFolderInspectorActive && _selectedPageFolder is not null;
        NoSelectionText.Visibility = Visibility.Collapsed;
        PageInspector.Visibility = !hasSelection && !hasPageFolderSelection ? Visibility.Visible : Visibility.Collapsed;
        PageFolderInspector.Visibility = hasPageFolderSelection ? Visibility.Visible : Visibility.Collapsed;
        WidgetInspector.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        if (_selectedWidget is not null)
        {
            var widget = _selectedWidget;
            var selectedCount = _selectedWidgetIds.Count;
            var isIndicator = widget.Type == HmiWidgetType.Indicator;
            var isNumericField = widget.Type is HmiWidgetType.ValueDisplay or HmiWidgetType.NumericInput;
            var supportsDynamics = SupportsDynamicAppearance(widget.Type);
            WidgetTypeText.Text = WidgetTypeLabel(widget.Type).ToUpperInvariant() + (selectedCount > 1 ? $" · RIFERIMENTO ({selectedCount} SELEZIONATI)" : string.Empty);
            WidgetTargetPageCombo.ItemsSource = widget.Type == HmiWidgetType.PopupButton
                ? _project.Pages.Where(page => page.Type == HmiPageType.Popup).ToList()
                : _project.Pages.Where(page => page.Type == HmiPageType.Standard).ToList();
            WidgetTagPropertyGroup.Visibility = widget.Type is HmiWidgetType.Button or HmiWidgetType.ValueDisplay or HmiWidgetType.NumericInput
                ? Visibility.Visible : Visibility.Collapsed;
            WidgetPagePropertyGroup.Visibility = widget.Type is HmiWidgetType.Navigation or HmiWidgetType.PopupButton ? Visibility.Visible : Visibility.Collapsed;
            WidgetRecipePropertyGroup.Visibility = widget.Type == HmiWidgetType.RecipeManager ? Visibility.Visible : Visibility.Collapsed;
            WidgetImagePropertyGroup.Visibility = widget.Type is HmiWidgetType.Image or HmiWidgetType.Button or HmiWidgetType.Navigation or HmiWidgetType.PopupButton or HmiWidgetType.PopupClose or HmiWidgetType.RuntimeExit or HmiWidgetType.LoginButton or HmiWidgetType.LogoutButton
                ? Visibility.Visible : Visibility.Collapsed;
            WidgetValuePropertyGroup.Visibility = widget.Type is HmiWidgetType.Button or HmiWidgetType.ValueDisplay or HmiWidgetType.NumericInput
                ? Visibility.Visible : Visibility.Collapsed;
            WidgetHistoryPropertyGroup.Visibility = widget.Type is HmiWidgetType.DataHistoryViewer or HmiWidgetType.TrendChart
                ? Visibility.Visible : Visibility.Collapsed;
            WidgetChartPropertyGroup.Visibility = widget.Type == HmiWidgetType.TrendChart ? Visibility.Visible : Visibility.Collapsed;
            WidgetAlarmHistoryPropertyGroup.Visibility = widget.Type == HmiWidgetType.AlarmHistoryViewer ? Visibility.Visible : Visibility.Collapsed;
            WidgetAnimationPropertyGroup.Visibility = supportsDynamics ? Visibility.Visible : Visibility.Collapsed;
            WidgetNumericAppearancePropertyGroup.Visibility = isNumericField ? Visibility.Visible : Visibility.Collapsed;
            WidgetTextPropertyGroup.Visibility = widget.Type is HmiWidgetType.Indicator or HmiWidgetType.Image ? Visibility.Collapsed : Visibility.Visible;
            WidgetFontSizePropertyGroup.Visibility = widget.Type is HmiWidgetType.Indicator or HmiWidgetType.Image ? Visibility.Collapsed : Visibility.Visible;
            WidgetTextAlignmentPropertyGroup.Visibility = SupportsTextAlignment(widget.Type) ? Visibility.Visible : Visibility.Collapsed;
            WidgetBackgroundPropertyGroup.Visibility = isIndicator ? Visibility.Collapsed : Visibility.Visible;
            WidgetForegroundPropertyGroup.Visibility = widget.Type is HmiWidgetType.Indicator or HmiWidgetType.Image ? Visibility.Collapsed : Visibility.Visible;
            WidgetConnectionsSection.Visibility = AnyVisible(WidgetTagPropertyGroup, WidgetPagePropertyGroup, WidgetRecipePropertyGroup, WidgetImagePropertyGroup);
            WidgetAppearanceSection.Visibility = AnyVisible(WidgetFontSizePropertyGroup, WidgetTextAlignmentPropertyGroup, WidgetBackgroundPropertyGroup, WidgetForegroundPropertyGroup);
            WidgetValuesSection.Visibility = WidgetValuePropertyGroup.Visibility;
            WidgetDataSection.Visibility = AnyVisible(WidgetHistoryPropertyGroup, WidgetChartPropertyGroup, WidgetAlarmHistoryPropertyGroup);
            WidgetDynamicsSection.Visibility = WidgetAnimationPropertyGroup.Visibility;
            WidgetAnimationRuleForegroundGroup.Visibility = isIndicator ? Visibility.Collapsed : Visibility.Visible;
            WidgetAnimationDefaultForegroundGroup.Visibility = isIndicator ? Visibility.Collapsed : Visibility.Visible;
            WidgetAnimationTitleText.Text = isIndicator ? "STATI DINAMICI DELLA SPIA" : "STATI DINAMICI A REGOLE";
            WidgetAnimationEnabledCheck.Content = isIndicator ? "Abilita dinamizzazione della spia" : "Abilita dinamizzazione da tag";
            WidgetAnimationTagLabel.Text = isIndicator ? "Tag stato" : "Tag sorgente";
            WidgetAnimationRuleBackgroundLabel.Text = isIndicator ? "COLORE DELLA SPIA" : "COLORE SFONDO";
            WidgetAnimationDefaultBackgroundLabel.Text = isIndicator ? "Colore spia predefinito" : "Colore sfondo predefinito";
            WidgetNameBox.Text = widget.Name;
            WidgetRequiredAccessLevelBox.Text = widget.RequiredAccessLevel.ToString(CultureInfo.InvariantCulture);
            WidgetRequiredAccessLevelBox.IsEnabled = widget.Type is not (HmiWidgetType.LoginButton or HmiWidgetType.LogoutButton);
            WidgetTextBox.Text = widget.Text;
            WidgetShowDescriptionCheck.IsChecked = widget.ShowDescription;
            WidgetShowBackgroundCheck.IsChecked = widget.ShowBackground;
            WidgetTagCombo.SelectedValue = widget.TagId;
            WidgetTargetPageCombo.SelectedValue = widget.TargetPageId;
            WidgetRecipeBookCombo.SelectedValue = widget.RecipeBookId;
            WidgetImageCombo.SelectedValue = widget.ImageAssetId;
            WidgetUseImageCheck.IsChecked = widget.UseImageAsContent;
            WidgetImageStretchCombo.SelectedItem = widget.ImageStretch;
            WidgetXBox.Text = FormatNumber(widget.X);
            WidgetYBox.Text = FormatNumber(widget.Y);
            WidgetWidthBox.Text = FormatNumber(widget.Width);
            WidgetHeightBox.Text = FormatNumber(widget.Height);
            WidgetFontSizeBox.Text = FormatNumber(widget.FontSize);
            WidgetBackgroundBox.Text = widget.Background;
            WidgetForegroundBox.Text = widget.Foreground;
            WidgetWriteValueBox.Text = widget.WriteValue;
            WidgetSuffixBox.Text = widget.Suffix;
            WidgetHistoryDatabaseBox.Text = widget.HistoryDatabaseName;
            WidgetHistoryTableBox.Text = widget.HistoryTableName;
            WidgetHistoryHoursBox.Text = widget.HistoryHours.ToString(CultureInfo.InvariantCulture);
            WidgetHistoryMaxRowsBox.Text = widget.HistoryMaxRows.ToString(CultureInfo.InvariantCulture);
            WidgetChartSourceCombo.SelectedItem = widget.ChartSource;
            RefreshChartSeriesEditor(widget, _editingChartSeriesId);
            WidgetAlarmHistoryRetentionBox.Text = widget.AlarmHistoryRetentionDays.ToString(CultureInfo.InvariantCulture);
            WidgetAnimationEnabledCheck.IsChecked = widget.Animation.Enabled;
            WidgetAnimationTagCombo.SelectedValue = widget.Animation.TagId;
            WidgetAnimationDefaultBackgroundBox.Text = widget.Animation.DefaultBackground;
            WidgetAnimationDefaultForegroundBox.Text = widget.Animation.DefaultForeground;
            RefreshAnimationRuleEditor(widget, _editingAnimationRuleId);
            UpdateAnimationValidation(widget);
            UpdateTextAlignmentButtons(widget.TextAlignment);
        }
        _updatingInspector = false;
    }

    private void UpdatePositionFields()
    {
        if (_selectedWidget is null)
        {
            return;
        }
        _updatingInspector = true;
        WidgetXBox.Text = FormatNumber(_selectedWidget.X);
        WidgetYBox.Text = FormatNumber(_selectedWidget.Y);
        _updatingInspector = false;
    }

    private void ApplyWidgetInspector()
    {
        if (_updatingInspector || _selectedWidget is null)
        {
            return;
        }

        var widget = _selectedWidget;
        var originalWidgetState = JsonSerializer.Serialize(widget);
        CommitAnimationRuleEditor();
        widget.Name = WidgetNameBox.Text.Trim();
        widget.RequiredAccessLevel = Math.Clamp(ParseInt(WidgetRequiredAccessLevelBox.Text, widget.RequiredAccessLevel), 0, _project.Security.MaximumAccessLevel);
        if (widget.Type is HmiWidgetType.LoginButton or HmiWidgetType.LogoutButton)
        {
            widget.RequiredAccessLevel = 0;
        }
        widget.Text = WidgetTextBox.Text;
        if (widget.Type is HmiWidgetType.ValueDisplay or HmiWidgetType.NumericInput)
        {
            widget.ShowDescription = WidgetShowDescriptionCheck.IsChecked == true;
            widget.ShowBackground = WidgetShowBackgroundCheck.IsChecked == true;
        }
        if (widget.Type == HmiWidgetType.TrendChart)
        {
            SyncLegacyChartTag(widget);
        }
        else if (widget.Type != HmiWidgetType.Indicator)
        {
            widget.TagId = WidgetTagCombo.SelectedValue as string ?? string.Empty;
        }
        widget.TargetPageId = WidgetTargetPageCombo.SelectedValue as string ?? string.Empty;
        widget.RecipeBookId = WidgetRecipeBookCombo.SelectedValue as string ?? string.Empty;
        widget.ImageAssetId = WidgetImageCombo.SelectedValue as string ?? string.Empty;
        widget.UseImageAsContent = WidgetUseImageCheck.IsChecked == true;
        widget.ImageStretch = WidgetImageStretchCombo.SelectedItem as string ?? "Uniform";
        widget.X = Clamp(ParseDouble(WidgetXBox.Text, widget.X), 0, (_selectedPage?.Width ?? _project.CanvasWidth) - widget.Width);
        widget.Y = Clamp(ParseDouble(WidgetYBox.Text, widget.Y), 0, (_selectedPage?.Height ?? _project.CanvasHeight) - widget.Height);
        widget.Width = Clamp(ParseDouble(WidgetWidthBox.Text, widget.Width), 70, (_selectedPage?.Width ?? _project.CanvasWidth) - widget.X);
        widget.Height = Clamp(ParseDouble(WidgetHeightBox.Text, widget.Height), 36, (_selectedPage?.Height ?? _project.CanvasHeight) - widget.Y);
        widget.FontSize = Clamp(ParseDouble(WidgetFontSizeBox.Text, widget.FontSize), 8, 72);
        widget.Background = WidgetBackgroundBox.Text.Trim();
        widget.Foreground = WidgetForegroundBox.Text.Trim();
        widget.WriteValue = WidgetWriteValueBox.Text.Trim();
        widget.Suffix = WidgetSuffixBox.Text;
        widget.HistoryDatabaseName = WidgetHistoryDatabaseBox.Text.Trim();
        widget.HistoryTableName = WidgetHistoryTableBox.Text.Trim();
        widget.HistoryHours = Math.Clamp(ParseInt(WidgetHistoryHoursBox.Text, widget.HistoryHours), 1, 24 * 365);
        widget.HistoryMaxRows = Math.Clamp(ParseInt(WidgetHistoryMaxRowsBox.Text, widget.HistoryMaxRows), 10, 10000);
        widget.ChartSource = WidgetChartSourceCombo.SelectedItem is ChartDataSource chartSource ? chartSource : ChartDataSource.LivePlc;
        widget.AlarmHistoryRetentionDays = Math.Clamp(ParseInt(WidgetAlarmHistoryRetentionBox.Text, widget.AlarmHistoryRetentionDays), 1, 3650);
        if (SupportsDynamicAppearance(widget.Type))
        {
            widget.Animation.Enabled = WidgetAnimationEnabledCheck.IsChecked == true;
            widget.Animation.TagId = WidgetAnimationTagCombo.SelectedValue as string ?? string.Empty;
            widget.Animation.DefaultBackground = string.IsNullOrWhiteSpace(WidgetAnimationDefaultBackgroundBox.Text)
                ? widget.Type == HmiWidgetType.Indicator ? "#526273" : widget.Background
                : WidgetAnimationDefaultBackgroundBox.Text.Trim();
            widget.Animation.DefaultForeground = string.IsNullOrWhiteSpace(WidgetAnimationDefaultForegroundBox.Text)
                ? widget.Foreground
                : WidgetAnimationDefaultForegroundBox.Text.Trim();
            if (widget.Type == HmiWidgetType.Indicator)
            {
                widget.TagId = widget.Animation.TagId;
            }
        }
        if (!string.Equals(originalWidgetState, JsonSerializer.Serialize(widget), StringComparison.Ordinal))
        {
            MarkDirty();
        }
        RenderDesigner();
        ShowWidgetInspector();
    }

    private void WidgetProperty_LostFocus(object sender, RoutedEventArgs e) => ApplyWidgetInspector();

    private void WidgetProperty_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingInspector)
        {
            ApplyWidgetInspector();
        }
    }

    private void WidgetOption_Click(object sender, RoutedEventArgs e)
    {
        if (!_updatingInspector)
        {
            ApplyWidgetInspector();
        }
    }

    private void WidgetTextAlignment_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || _selectedWidget is null || sender is not Button { Tag: string alignmentName } ||
            !Enum.TryParse<HmiTextAlignment>(alignmentName, out var alignment))
        {
            return;
        }
        _selectedWidget.TextAlignment = alignment;
        UpdateTextAlignmentButtons(alignment);
        MarkDirty();
        RenderDesigner();
    }

    private void UpdateTextAlignmentButtons(HmiTextAlignment alignment)
    {
        var effective = alignment == HmiTextAlignment.Default && _selectedWidget is not null
            ? DefaultTextAlignment(_selectedWidget.Type)
            : alignment;
        foreach (var button in new[] { WidgetTextAlignLeftButton, WidgetTextAlignCenterButton, WidgetTextAlignRightButton })
        {
            var selected = string.Equals(button.Tag as string, effective.ToString(), StringComparison.Ordinal);
            button.Background = BrushOf(selected ? "#174341" : "#182431");
            button.BorderBrush = BrushOf(selected ? "#28C2B8" : "#263545");
        }
    }

    private void BackgroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            WidgetBackgroundBox.Text = color;
            ApplyWidgetInspector();
        }
    }

    private void ForegroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            WidgetForegroundBox.Text = color;
            ApplyWidgetInspector();
        }
    }

    private void AnimationOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || _selectedWidget is null || !SupportsDynamicAppearance(_selectedWidget.Type))
        {
            return;
        }
        _selectedWidget.Animation.Enabled = WidgetAnimationEnabledCheck.IsChecked == true;
        SyncLegacyAnimationFields(_selectedWidget.Animation);
        MarkDirty();
        RenderDesigner();
        UpdateAnimationValidation(_selectedWidget);
    }

    private void WidgetAnimationTagCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || _selectedWidget is null || !SupportsDynamicAppearance(_selectedWidget.Type))
        {
            return;
        }
        CommitAnimationRuleEditor();
        _selectedWidget.Animation.TagId = WidgetAnimationTagCombo.SelectedValue as string ?? string.Empty;
        if (_selectedWidget.Type == HmiWidgetType.Indicator)
        {
            _selectedWidget.TagId = _selectedWidget.Animation.TagId;
        }
        MarkDirty();
        RefreshAnimationRuleEditor(_selectedWidget, _editingAnimationRuleId);
        UpdateAnimationValidation(_selectedWidget);
    }

    private void RefreshAnimationRuleEditor(HmiWidgetDefinition widget, string? selectedRuleId = null)
    {
        var wasUpdating = _updatingInspector;
        _updatingInspector = true;
        try
        {
            if (!SupportsDynamicAppearance(widget.Type))
            {
                WidgetAnimationRuleList.ItemsSource = null;
                WidgetAnimationRuleEditor.Visibility = Visibility.Collapsed;
                _editingAnimationRuleId = null;
                return;
            }
            var items = widget.Animation.Rules.Select((rule, index) => new AnimationRuleEditorItem(
                rule,
                index + 1,
                string.IsNullOrWhiteSpace(rule.Name) ? $"Stato {index + 1}" : rule.Name,
                DynamicConditionSummary(rule),
                string.IsNullOrWhiteSpace(rule.Background) ? widget.Animation.DefaultBackground : rule.Background)).ToList();
            WidgetAnimationRuleList.ItemsSource = null;
            WidgetAnimationRuleList.ItemsSource = items;
            var selected = items.FirstOrDefault(item => item.Rule.Id == selectedRuleId)
                ?? items.FirstOrDefault(item => item.Rule.Id == _editingAnimationRuleId)
                ?? items.FirstOrDefault();
            WidgetAnimationRuleList.SelectedItem = selected;
            _editingAnimationRuleId = selected?.Rule.Id;
            PopulateAnimationRuleEditor(widget, selected?.Rule);
        }
        finally
        {
            _updatingInspector = wasUpdating;
        }
    }

    private void PopulateAnimationRuleEditor(HmiWidgetDefinition widget, HmiAnimationRuleDefinition? rule)
    {
        WidgetAnimationRuleEditor.Visibility = rule is null ? Visibility.Collapsed : Visibility.Visible;
        WidgetAnimationRuleNameBox.Text = rule?.Name ?? string.Empty;
        WidgetAnimationRuleEnabledCheck.IsChecked = rule?.Enabled == true;
        var sourceType = GetAnimationSourceTag(widget)?.DataType;
        var conditionOptions = AnimationConditionOptions
            .Where(option => IsDynamicConditionCompatible(sourceType, option.Value))
            .ToList();
        if (rule is not null && conditionOptions.All(option => option.Value != rule.Condition))
        {
            var legacyOption = AnimationConditionOptions.First(option => option.Value == rule.Condition);
            conditionOptions.Add(new AnimationConditionOption(legacyOption.Value, legacyOption.Label + " (non compatibile)"));
        }
        WidgetAnimationRuleConditionCombo.ItemsSource = conditionOptions;
        WidgetAnimationRuleConditionCombo.SelectedValue = rule?.Condition ?? HmiDynamicCondition.Equals;
        WidgetAnimationOperand1Box.Text = rule?.CompareValue ?? string.Empty;
        WidgetAnimationOperand2Box.Text = rule?.CompareValue2 ?? string.Empty;
        WidgetAnimationRuleBackgroundBox.Text = rule?.Background ?? string.Empty;
        WidgetAnimationRuleForegroundBox.Text = rule?.Foreground ?? string.Empty;
        UpdateAnimationConditionEditor(rule?.Condition ?? HmiDynamicCondition.Equals);
    }

    private void WidgetAnimationRuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || WidgetAnimationRuleList.SelectedItem is not AnimationRuleEditorItem item)
        {
            return;
        }
        if (_editingAnimationRuleId == item.Rule.Id)
        {
            return;
        }
        CommitAnimationRuleEditor();
        _editingAnimationRuleId = item.Rule.Id;
        if (_selectedWidget is not null)
        {
            RefreshAnimationRuleEditor(_selectedWidget, item.Rule.Id);
        }
    }

    private void WidgetAnimationRuleConditionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingInspector && WidgetAnimationRuleConditionCombo.SelectedValue is HmiDynamicCondition condition)
        {
            UpdateAnimationConditionEditor(condition);
            CommitAnimationRuleEditor();
        }
    }

    private void UpdateAnimationConditionEditor(HmiDynamicCondition condition)
    {
        var hasFirstOperand = condition is not (HmiDynamicCondition.True or HmiDynamicCondition.False);
        var hasSecondOperand = condition is HmiDynamicCondition.BetweenInclusive or HmiDynamicCondition.BitMaskEquals;
        WidgetAnimationOperand1Group.Visibility = hasFirstOperand ? Visibility.Visible : Visibility.Collapsed;
        WidgetAnimationOperand2Group.Visibility = hasSecondOperand ? Visibility.Visible : Visibility.Collapsed;
        WidgetAnimationOperand1Label.Text = condition switch
        {
            HmiDynamicCondition.BetweenInclusive => "Valore minimo incluso",
            HmiDynamicCondition.BitSet or HmiDynamicCondition.BitClear => $"Indice bit (0-{GetAnimationMaximumBit()})",
            HmiDynamicCondition.BitMaskEquals => "Maschera bit (decimale o 0x...) ",
            _ => "Valore confronto"
        };
        WidgetAnimationOperand2Label.Text = condition == HmiDynamicCondition.BitMaskEquals
            ? "Valore atteso dopo la maschera"
            : "Valore massimo incluso";
    }

    private void NewAnimationRule_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget is null || !SupportsDynamicAppearance(_selectedWidget.Type))
        {
            return;
        }
        CommitAnimationRuleEditor();
        var sourceType = GetAnimationSourceTag(_selectedWidget)?.DataType;
        var rule = new HmiAnimationRuleDefinition
        {
            Name = $"Stato {_selectedWidget.Animation.Rules.Count + 1}",
            Condition = sourceType == TagDataType.Bool ? HmiDynamicCondition.True : HmiDynamicCondition.Equals,
            CompareValue = sourceType == TagDataType.String ? string.Empty : "0",
            Background = ChartSeriesPalette[_selectedWidget.Animation.Rules.Count % ChartSeriesPalette.Length],
            Foreground = _selectedWidget.Foreground
        };
        _selectedWidget.Animation.Rules.Add(rule);
        _editingAnimationRuleId = rule.Id;
        WidgetDynamicsSection.IsExpanded = true;
        SyncLegacyAnimationFields(_selectedWidget.Animation);
        MarkDirty();
        RenderDesigner();
        RefreshAnimationRuleEditor(_selectedWidget, rule.Id);
        UpdateAnimationValidation(_selectedWidget);
    }

    private void DuplicateAnimationRule_Click(object sender, RoutedEventArgs e)
    {
        CommitAnimationRuleEditor();
        if (_selectedWidget is null || GetEditingAnimationRule() is not { } source)
        {
            return;
        }
        var duplicate = new HmiAnimationRuleDefinition
        {
            Name = source.Name + " copia",
            Enabled = source.Enabled,
            Condition = source.Condition,
            CompareValue = source.CompareValue,
            CompareValue2 = source.CompareValue2,
            Background = source.Background,
            Foreground = source.Foreground
        };
        var sourceIndex = _selectedWidget.Animation.Rules.IndexOf(source);
        _selectedWidget.Animation.Rules.Insert(sourceIndex + 1, duplicate);
        _editingAnimationRuleId = duplicate.Id;
        SyncLegacyAnimationFields(_selectedWidget.Animation);
        MarkDirty();
        RenderDesigner();
        RefreshAnimationRuleEditor(_selectedWidget, duplicate.Id);
        UpdateAnimationValidation(_selectedWidget);
    }

    private void MoveAnimationRuleUp_Click(object sender, RoutedEventArgs e) => MoveAnimationRule(-1);
    private void MoveAnimationRuleDown_Click(object sender, RoutedEventArgs e) => MoveAnimationRule(1);

    private void MoveAnimationRule(int direction)
    {
        CommitAnimationRuleEditor();
        if (_selectedWidget is null || GetEditingAnimationRule() is not { } rule)
        {
            return;
        }
        var rules = _selectedWidget.Animation.Rules;
        var index = rules.IndexOf(rule);
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= rules.Count)
        {
            return;
        }
        rules.RemoveAt(index);
        rules.Insert(targetIndex, rule);
        SyncLegacyAnimationFields(_selectedWidget.Animation);
        MarkDirty();
        RenderDesigner();
        RefreshAnimationRuleEditor(_selectedWidget, rule.Id);
        UpdateAnimationValidation(_selectedWidget);
    }

    private void DeleteAnimationRule_Click(object sender, RoutedEventArgs e)
    {
        CommitAnimationRuleEditor();
        if (_selectedWidget is null || GetEditingAnimationRule() is not { } rule)
        {
            return;
        }
        var rules = _selectedWidget.Animation.Rules;
        var removedIndex = rules.IndexOf(rule);
        rules.Remove(rule);
        var next = rules.ElementAtOrDefault(Math.Min(Math.Max(0, removedIndex), Math.Max(0, rules.Count - 1)));
        _editingAnimationRuleId = next?.Id;
        SyncLegacyAnimationFields(_selectedWidget.Animation);
        MarkDirty();
        RenderDesigner();
        RefreshAnimationRuleEditor(_selectedWidget, next?.Id);
        UpdateAnimationValidation(_selectedWidget);
    }

    private void SaveAnimationRule_Click(object sender, RoutedEventArgs e) => SaveAnimationRuleFromEditor();

    private void SaveAnimationRuleFromEditor()
    {
        if (_selectedWidget is null || GetEditingAnimationRule() is not { } rule)
        {
            return;
        }
        CommitAnimationRuleEditor();
        RenderDesigner();
        RefreshAnimationRuleEditor(_selectedWidget, rule.Id);
        UpdateAnimationValidation(_selectedWidget);
    }

    private bool CommitAnimationRuleEditor()
    {
        if (_updatingInspector || _selectedWidget is null || GetEditingAnimationRule() is not { } rule)
        {
            return false;
        }

        var name = string.IsNullOrWhiteSpace(WidgetAnimationRuleNameBox.Text) ? "Stato" : WidgetAnimationRuleNameBox.Text.Trim();
        var enabled = WidgetAnimationRuleEnabledCheck.IsChecked == true;
        var condition = WidgetAnimationRuleConditionCombo.SelectedValue is HmiDynamicCondition selectedCondition
            ? selectedCondition
            : rule.Condition;
        var compareValue = WidgetAnimationOperand1Box.Text.Trim();
        var compareValue2 = WidgetAnimationOperand2Box.Text.Trim();
        var background = string.IsNullOrWhiteSpace(WidgetAnimationRuleBackgroundBox.Text)
            ? _selectedWidget.Animation.DefaultBackground
            : WidgetAnimationRuleBackgroundBox.Text.Trim();
        var foreground = string.IsNullOrWhiteSpace(WidgetAnimationRuleForegroundBox.Text)
            ? _selectedWidget.Animation.DefaultForeground
            : WidgetAnimationRuleForegroundBox.Text.Trim();
        var changed = rule.Name != name || rule.Enabled != enabled || rule.Condition != condition ||
            rule.CompareValue != compareValue || rule.CompareValue2 != compareValue2 ||
            rule.Background != background || rule.Foreground != foreground;
        if (!changed)
        {
            return false;
        }

        rule.Name = name;
        rule.Enabled = enabled;
        rule.Condition = condition;
        rule.CompareValue = compareValue;
        rule.CompareValue2 = compareValue2;
        rule.Background = background;
        rule.Foreground = foreground;
        SyncLegacyAnimationFields(_selectedWidget.Animation);
        MarkDirty();
        UpdateAnimationValidation(_selectedWidget);
        return true;
    }

    private void AnimationRuleEditor_LostFocus(object sender, RoutedEventArgs e) => CommitAnimationRuleEditor();

    private void AnimationRuleEditor_Click(object sender, RoutedEventArgs e) => CommitAnimationRuleEditor();

    private HmiAnimationRuleDefinition? GetEditingAnimationRule() => _selectedWidget?.Animation.Rules
        .FirstOrDefault(rule => rule.Id == _editingAnimationRuleId);

    private void AnimationRuleBackgroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            WidgetAnimationRuleBackgroundBox.Text = color;
            SaveAnimationRuleFromEditor();
        }
    }

    private void AnimationRuleForegroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            WidgetAnimationRuleForegroundBox.Text = color;
            SaveAnimationRuleFromEditor();
        }
    }

    private void AnimationDefaultBackgroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            WidgetAnimationDefaultBackgroundBox.Text = color;
            SaveAnimationDefaults();
        }
    }

    private void AnimationDefaultForegroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            WidgetAnimationDefaultForegroundBox.Text = color;
            SaveAnimationDefaults();
        }
    }

    private void AnimationDefault_LostFocus(object sender, RoutedEventArgs e) => SaveAnimationDefaults();

    private void SaveAnimationDefaults()
    {
        if (_updatingInspector || _selectedWidget is null || !SupportsDynamicAppearance(_selectedWidget.Type))
        {
            return;
        }
        _selectedWidget.Animation.DefaultBackground = string.IsNullOrWhiteSpace(WidgetAnimationDefaultBackgroundBox.Text)
            ? _selectedWidget.Type == HmiWidgetType.Indicator ? "#526273" : _selectedWidget.Background
            : WidgetAnimationDefaultBackgroundBox.Text.Trim();
        _selectedWidget.Animation.DefaultForeground = string.IsNullOrWhiteSpace(WidgetAnimationDefaultForegroundBox.Text)
            ? _selectedWidget.Foreground
            : WidgetAnimationDefaultForegroundBox.Text.Trim();
        SyncLegacyAnimationFields(_selectedWidget.Animation);
        MarkDirty();
        RenderDesigner();
        UpdateAnimationValidation(_selectedWidget);
    }

    private TagDefinition? GetAnimationSourceTag(HmiWidgetDefinition widget) =>
        _project.Tags.FirstOrDefault(tag => tag.Id == widget.Animation.TagId);

    private int GetAnimationMaximumBit() => GetDynamicMaximumBit(
        _selectedWidget is null ? null : GetAnimationSourceTag(_selectedWidget)?.DataType);

    private static int GetDynamicMaximumBit(TagDataType? dataType) => dataType == TagDataType.Int ? 15 : 31;

    private static bool IsDynamicConditionCompatible(TagDataType? dataType, HmiDynamicCondition condition) => dataType switch
    {
        null => true,
        TagDataType.Bool => condition is HmiDynamicCondition.True or HmiDynamicCondition.False or
            HmiDynamicCondition.Equals or HmiDynamicCondition.NotEquals,
        TagDataType.String => condition is HmiDynamicCondition.Equals or HmiDynamicCondition.NotEquals,
        TagDataType.Real => condition is HmiDynamicCondition.Equals or HmiDynamicCondition.NotEquals or
            HmiDynamicCondition.GreaterThan or HmiDynamicCondition.GreaterThanOrEqual or
            HmiDynamicCondition.LessThan or HmiDynamicCondition.LessThanOrEqual or HmiDynamicCondition.BetweenInclusive,
        TagDataType.Int or TagDataType.DInt => condition is HmiDynamicCondition.Equals or HmiDynamicCondition.NotEquals or
            HmiDynamicCondition.GreaterThan or HmiDynamicCondition.GreaterThanOrEqual or
            HmiDynamicCondition.LessThan or HmiDynamicCondition.LessThanOrEqual or HmiDynamicCondition.BetweenInclusive or
            HmiDynamicCondition.BitSet or HmiDynamicCondition.BitClear or HmiDynamicCondition.BitMaskEquals,
        _ => false
    };

    private void UpdateAnimationValidation(HmiWidgetDefinition widget)
    {
        var issues = new List<string>();
        var sourceTag = GetAnimationSourceTag(widget);
        if (widget.Animation.Enabled && string.IsNullOrWhiteSpace(widget.Animation.TagId))
        {
            issues.Add("Selezionare una tag sorgente.");
        }
        else if (widget.Animation.Enabled && sourceTag is null)
        {
            issues.Add("La tag sorgente non è disponibile.");
        }
        else if (widget.Animation.Enabled && sourceTag?.Access == TagAccess.Write)
        {
            issues.Add("La tag sorgente è di sola scrittura e non può pilotare una dinamica.");
        }
        if (widget.Animation.Enabled && widget.Animation.Rules.All(rule => !rule.Enabled))
        {
            issues.Add("Nessuna regola è abilitata: verrà usato sempre lo stato predefinito.");
        }
        if (!IsValidBrushCode(widget.Animation.DefaultBackground))
        {
            issues.Add("Il colore predefinito di sfondo / riempimento non è valido.");
        }
        if (widget.Type != HmiWidgetType.Indicator && !IsValidBrushCode(widget.Animation.DefaultForeground))
        {
            issues.Add("Il colore predefinito del testo non è valido.");
        }
        foreach (var rule in widget.Animation.Rules.Where(rule => rule.Enabled))
        {
            if (!TryValidateDynamicRule(rule, sourceTag, out var issue))
            {
                issues.Add($"{rule.Name}: {issue}");
            }
            if (!IsValidBrushCode(rule.Background))
            {
                issues.Add($"{rule.Name}: il colore di sfondo / riempimento non è valido");
            }
            if (widget.Type != HmiWidgetType.Indicator && !IsValidBrushCode(rule.Foreground))
            {
                issues.Add($"{rule.Name}: il colore del testo non è valido");
            }
        }
        var ranges = widget.Animation.Rules.Where(rule => rule.Enabled && rule.Condition == HmiDynamicCondition.BetweenInclusive)
            .Select(rule => new { Rule = rule, Min = TryParseDynamicNumber(rule.CompareValue, out var min) ? min : double.NaN, Max = TryParseDynamicNumber(rule.CompareValue2, out var max) ? max : double.NaN })
            .Where(item => !double.IsNaN(item.Min) && !double.IsNaN(item.Max) && item.Min <= item.Max)
            .ToList();
        for (var first = 0; first < ranges.Count; first++)
        {
            for (var second = first + 1; second < ranges.Count; second++)
            {
                if (ranges[first].Min <= ranges[second].Max && ranges[second].Min <= ranges[first].Max)
                {
                    issues.Add($"Gli intervalli '{ranges[first].Rule.Name}' e '{ranges[second].Rule.Name}' si sovrappongono: prevale il primo.");
                }
            }
        }
        WidgetAnimationValidationText.Text = string.Join(Environment.NewLine, issues.Distinct());
        WidgetAnimationValidationText.Visibility = issues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool TryValidateDynamicRule(HmiAnimationRuleDefinition rule, TagDefinition? sourceTag, out string issue)
    {
        issue = string.Empty;
        if (sourceTag is not null && !IsDynamicConditionCompatible(sourceTag.DataType, rule.Condition))
        {
            issue = $"la condizione non è compatibile con una tag {sourceTag.DataType}";
            return false;
        }
        switch (rule.Condition)
        {
            case HmiDynamicCondition.True:
            case HmiDynamicCondition.False:
                return true;
            case HmiDynamicCondition.Equals:
            case HmiDynamicCondition.NotEquals:
                if (TryValidateDynamicEqualityOperand(rule.CompareValue, sourceTag?.DataType))
                {
                    return true;
                }
                issue = sourceTag?.DataType switch
                {
                    TagDataType.Bool => "il valore di confronto deve essere booleano (true/false, vero/falso oppure 1/0)",
                    TagDataType.Int or TagDataType.DInt => "il valore di confronto deve essere un intero valido per la tag",
                    TagDataType.Real => "il valore di confronto deve essere numerico e finito",
                    _ => "indicare il valore di confronto"
                };
                return false;
            case HmiDynamicCondition.GreaterThan:
            case HmiDynamicCondition.GreaterThanOrEqual:
            case HmiDynamicCondition.LessThan:
            case HmiDynamicCondition.LessThanOrEqual:
                if (TryValidateDynamicNumericOperand(rule.CompareValue, sourceTag?.DataType))
                {
                    return true;
                }
                issue = "il valore di confronto deve essere numerico";
                return false;
            case HmiDynamicCondition.BetweenInclusive:
                if (!TryValidateDynamicNumericOperand(rule.CompareValue, sourceTag?.DataType) ||
                    !TryValidateDynamicNumericOperand(rule.CompareValue2, sourceTag?.DataType) ||
                    !TryParseDynamicNumber(rule.CompareValue, out var minimum) ||
                    !TryParseDynamicNumber(rule.CompareValue2, out var maximum))
                {
                    issue = "i limiti dell'intervallo devono essere numerici";
                    return false;
                }
                if (minimum > maximum)
                {
                    issue = "il limite minimo è maggiore del massimo";
                    return false;
                }
                return true;
            case HmiDynamicCondition.BitSet:
            case HmiDynamicCondition.BitClear:
                var maximumBit = GetDynamicMaximumBit(sourceTag?.DataType);
                if (int.TryParse(rule.CompareValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bit) && bit >= 0 && bit <= maximumBit)
                {
                    return true;
                }
                issue = $"l'indice bit deve essere compreso tra 0 e {maximumBit}";
                return false;
            case HmiDynamicCondition.BitMaskEquals:
                if (TryGetDynamicUnsignedInteger(rule.CompareValue, sourceTag?.DataType, out _) &&
                    TryGetDynamicUnsignedInteger(rule.CompareValue2, sourceTag?.DataType, out _))
                {
                    return true;
                }
                issue = "maschera e valore atteso devono essere interi";
                return false;
            default:
                issue = "condizione non supportata";
                return false;
        }
    }

    private static bool TryValidateDynamicEqualityOperand(string? value, TagDataType? dataType) => dataType switch
    {
        TagDataType.Bool => TryConvertDynamicBoolean(value, out _),
        TagDataType.Int or TagDataType.DInt or TagDataType.Real => TryValidateDynamicNumericOperand(value, dataType),
        _ => !string.IsNullOrWhiteSpace(value)
    };

    private static bool TryValidateDynamicNumericOperand(string? value, TagDataType? dataType)
    {
        if (!TryParseDynamicNumber(value, out var number))
        {
            return false;
        }
        return dataType switch
        {
            TagDataType.Int => number == Math.Truncate(number) && number >= short.MinValue && number <= short.MaxValue,
            TagDataType.DInt => number == Math.Truncate(number) && number >= int.MinValue && number <= int.MaxValue,
            TagDataType.Real or null => true,
            _ => false
        };
    }

    private static string DynamicConditionSummary(HmiAnimationRuleDefinition rule) => rule.Condition switch
    {
        HmiDynamicCondition.True => "Valore vero",
        HmiDynamicCondition.False => "Valore falso",
        HmiDynamicCondition.Equals => $"Valore = {rule.CompareValue}",
        HmiDynamicCondition.NotEquals => $"Valore ≠ {rule.CompareValue}",
        HmiDynamicCondition.GreaterThan => $"Valore > {rule.CompareValue}",
        HmiDynamicCondition.GreaterThanOrEqual => $"Valore ≥ {rule.CompareValue}",
        HmiDynamicCondition.LessThan => $"Valore < {rule.CompareValue}",
        HmiDynamicCondition.LessThanOrEqual => $"Valore ≤ {rule.CompareValue}",
        HmiDynamicCondition.BetweenInclusive => $"{rule.CompareValue} ≤ valore ≤ {rule.CompareValue2}",
        HmiDynamicCondition.BitSet => $"Bit {rule.CompareValue} = 1",
        HmiDynamicCondition.BitClear => $"Bit {rule.CompareValue} = 0",
        HmiDynamicCondition.BitMaskEquals => $"(valore & {rule.CompareValue}) = {rule.CompareValue2}",
        _ => rule.Condition.ToString()
    } + (rule.Enabled ? string.Empty : " · disabilitata");

    private static void SyncLegacyAnimationFields(HmiAnimationDefinition animation)
    {
        animation.InactiveBackground = animation.DefaultBackground;
        animation.InactiveForeground = animation.DefaultForeground;
        var first = animation.Rules.FirstOrDefault(rule => rule.Enabled) ?? animation.Rules.FirstOrDefault();
        if (first is null)
        {
            return;
        }
        animation.Condition = first.Condition switch
        {
            HmiDynamicCondition.True => AlarmCondition.True,
            HmiDynamicCondition.False => AlarmCondition.False,
            HmiDynamicCondition.NotEquals => AlarmCondition.NotEquals,
            HmiDynamicCondition.GreaterThan or HmiDynamicCondition.GreaterThanOrEqual => AlarmCondition.GreaterThan,
            HmiDynamicCondition.LessThan or HmiDynamicCondition.LessThanOrEqual => AlarmCondition.LessThan,
            _ => AlarmCondition.Equals
        };
        animation.CompareValue = first.CompareValue;
        animation.ActiveBackground = first.Background;
        animation.ActiveForeground = first.Foreground;
    }

    private static Visibility AnyVisible(params UIElement[] elements) =>
        elements.Any(element => element.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshChartSeriesEditor(HmiWidgetDefinition widget, string? selectedSeriesId = null)
    {
        var wasUpdating = _updatingInspector;
        _updatingInspector = true;
        try
        {
            if (widget.Type != HmiWidgetType.TrendChart)
            {
                WidgetChartSeriesList.ItemsSource = null;
                WidgetChartSeriesTagCombo.SelectedIndex = -1;
                WidgetChartSeriesNameBox.Clear();
                WidgetChartSeriesColorBox.Clear();
                _editingChartSeriesId = null;
                return;
            }

            var items = widget.ChartSeries.Select(series => new ChartSeriesEditorItem(
                series,
                string.IsNullOrWhiteSpace(series.DisplayName) ? "Serie" : series.DisplayName,
                _project.Tags.FirstOrDefault(tag => tag.Id == series.TagId)?.Name ?? "Tag non disponibile",
                IsValidChartSeriesColor(series.Color) ? series.Color : "#28C2B8")).ToList();
            WidgetChartSeriesList.ItemsSource = null;
            WidgetChartSeriesList.ItemsSource = items;
            var selectedItem = items.FirstOrDefault(item => item.Series.Id == selectedSeriesId)
                ?? items.FirstOrDefault(item => item.Series.Id == _editingChartSeriesId)
                ?? items.FirstOrDefault();
            WidgetChartSeriesList.SelectedItem = selectedItem;
            _editingChartSeriesId = selectedItem?.Series.Id;
            PopulateChartSeriesEditor(selectedItem?.Series);
        }
        finally
        {
            _updatingInspector = wasUpdating;
        }
    }

    private void PopulateChartSeriesEditor(ChartSeriesDefinition? series)
    {
        WidgetChartSeriesTagCombo.SelectedValue = series?.TagId ?? string.Empty;
        WidgetChartSeriesNameBox.Text = series?.DisplayName ?? string.Empty;
        WidgetChartSeriesColorBox.Text = series?.Color ?? GetNextChartSeriesColor(_selectedWidget);
    }

    private void WidgetChartSeriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || WidgetChartSeriesList.SelectedItem is not ChartSeriesEditorItem item)
        {
            return;
        }
        _editingChartSeriesId = item.Series.Id;
        var wasUpdating = _updatingInspector;
        _updatingInspector = true;
        PopulateChartSeriesEditor(item.Series);
        _updatingInspector = wasUpdating;
    }

    private void WidgetChartSeriesTagCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || WidgetChartSeriesTagCombo.SelectedItem is not TagDefinition selectedTag)
        {
            return;
        }
        var currentSeries = _selectedWidget?.ChartSeries.FirstOrDefault(series => series.Id == _editingChartSeriesId);
        var previousTagName = currentSeries is null ? string.Empty : _project.Tags.FirstOrDefault(tag => tag.Id == currentSeries.TagId)?.Name ?? string.Empty;
        if (currentSeries is null || string.IsNullOrWhiteSpace(WidgetChartSeriesNameBox.Text) || WidgetChartSeriesNameBox.Text.Equals(previousTagName, StringComparison.Ordinal))
        {
            WidgetChartSeriesNameBox.Text = selectedTag.Name;
        }
    }

    private void NewChartSeries_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget is not { Type: HmiWidgetType.TrendChart } widget)
        {
            return;
        }
        _editingChartSeriesId = null;
        var usedTagIds = widget.ChartSeries.Select(series => series.TagId).ToHashSet();
        var tag = _project.Tags.FirstOrDefault(item => !usedTagIds.Contains(item.Id) && IsChartCompatibleTag(item));
        var wasUpdating = _updatingInspector;
        _updatingInspector = true;
        WidgetChartSeriesList.SelectedItem = null;
        WidgetChartSeriesTagCombo.SelectedValue = tag?.Id ?? string.Empty;
        WidgetChartSeriesNameBox.Text = tag?.Name ?? string.Empty;
        WidgetChartSeriesColorBox.Text = GetNextChartSeriesColor(widget);
        _updatingInspector = wasUpdating;
        WidgetChartSeriesNameBox.Focus();
        WidgetChartSeriesNameBox.SelectAll();
    }

    private void SaveChartSeries_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget is not { Type: HmiWidgetType.TrendChart } widget ||
            WidgetChartSeriesTagCombo.SelectedItem is not TagDefinition tag)
        {
            MessageBox.Show("Selezionare la tag da aggiungere al grafico.", "Serie grafico", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var duplicate = widget.ChartSeries.FirstOrDefault(series => series.TagId == tag.Id && series.Id != _editingChartSeriesId);
        if (duplicate is not null)
        {
            MessageBox.Show($"La tag '{tag.Name}' è già presente nel grafico.", "Serie grafico", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var color = WidgetChartSeriesColorBox.Text.Trim();
        if (!IsValidChartSeriesColor(color))
        {
            MessageBox.Show("Inserire un colore valido, ad esempio #28C2B8.", "Serie grafico", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var series = widget.ChartSeries.FirstOrDefault(item => item.Id == _editingChartSeriesId);
        if (series is null)
        {
            series = new ChartSeriesDefinition();
            widget.ChartSeries.Add(series);
        }
        series.TagId = tag.Id;
        series.DisplayName = string.IsNullOrWhiteSpace(WidgetChartSeriesNameBox.Text) ? tag.Name : WidgetChartSeriesNameBox.Text.Trim();
        series.Color = color;
        _editingChartSeriesId = series.Id;
        SyncLegacyChartTag(widget);
        MarkDirty();
        RenderDesigner();
        RefreshChartSeriesEditor(widget, series.Id);
        StatusText.Text = $"Serie '{series.DisplayName}' salvata nel grafico";
    }

    private void DeleteChartSeries_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget is not { Type: HmiWidgetType.TrendChart } widget)
        {
            return;
        }
        var series = widget.ChartSeries.FirstOrDefault(item => item.Id == _editingChartSeriesId);
        if (series is null || MessageBox.Show($"Eliminare la serie '{series.DisplayName}'?", "Serie grafico",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var removedIndex = widget.ChartSeries.IndexOf(series);
        widget.ChartSeries.Remove(series);
        var nextSeries = widget.ChartSeries.ElementAtOrDefault(Math.Min(removedIndex, Math.Max(0, widget.ChartSeries.Count - 1)));
        _editingChartSeriesId = nextSeries?.Id;
        SyncLegacyChartTag(widget);
        MarkDirty();
        RenderDesigner();
        RefreshChartSeriesEditor(widget, _editingChartSeriesId);
        StatusText.Text = "Serie rimossa dal grafico";
    }

    private void ChartSeriesColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            WidgetChartSeriesColorBox.Text = color;
        }
    }

    private static bool IsValidChartSeriesColor(string color)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(color) && new BrushConverter().ConvertFromString(color) is Brush;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsChartCompatibleTag(TagDefinition tag) =>
        tag.Access != TagAccess.Write && tag.DataType != TagDataType.String;

    private static void SyncLegacyChartTag(HmiWidgetDefinition widget) =>
        widget.TagId = widget.ChartSeries.FirstOrDefault()?.TagId ?? string.Empty;

    private static string GetNextChartSeriesColor(HmiWidgetDefinition? widget)
    {
        if (widget is null)
        {
            return ChartSeriesPalette[0];
        }
        var usedColors = widget.ChartSeries.Select(series => series.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ChartSeriesPalette.FirstOrDefault(color => !usedColors.Contains(color))
            ?? ChartSeriesPalette[widget.ChartSeries.Count % ChartSeriesPalette.Length];
    }

    private void ImportWidgetImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget is null)
        {
            return;
        }
        var asset = ImportImageAsset();
        if (asset is null)
        {
            return;
        }
        _selectedWidget.ImageAssetId = asset.Id;
        _selectedWidget.UseImageAsContent = true;
        MarkDirty();
        RefreshCollections();
        ShowWidgetInspector();
        RenderDesigner();
    }

    private ProjectAssetDefinition? ImportImageAsset()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importa immagine nel progetto HMI",
            Filter = "Immagini (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Tutti i file (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }
        var fileInfo = new FileInfo(dialog.FileName);
        if (fileInfo.Length > 15 * 1024 * 1024)
        {
            MessageBox.Show("L'immagine supera il limite di 15 MB.", "Importa immagine", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var extension = fileInfo.Extension.ToLowerInvariant();
        var asset = new ProjectAssetDefinition
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(fileInfo.Name),
            FileName = fileInfo.Name,
            MimeType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                _ => "image/png"
            },
            DataBase64 = Convert.ToBase64String(File.ReadAllBytes(dialog.FileName))
        };
        _project.Assets.Add(asset);
        return asset;
    }

    private void AddWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPage is null || sender is not Button { Tag: string typeName } || !Enum.TryParse<HmiWidgetType>(typeName, out var type))
        {
            return;
        }

        var widget = CreateDefaultWidget(type, _selectedPage.Widgets.Count);
        if (type == HmiWidgetType.Image)
        {
            var asset = ImportImageAsset();
            if (asset is null)
            {
                return;
            }
            widget.ImageAssetId = asset.Id;
            widget.UseImageAsContent = true;
        }
        _selectedPage.Widgets.Add(widget);
        SelectOnlyWidget(widget);
        _pageFolderInspectorActive = false;
        MarkDirty();
        RenderDesigner();
        ShowWidgetInspector();
    }

    private HmiWidgetDefinition CreateDefaultWidget(HmiWidgetType type, int index)
    {
        var widget = new HmiWidgetDefinition
        {
            Type = type,
            Name = WidgetTypeLabel(type),
            Text = WidgetTypeLabel(type),
            X = 40 + index % 8 * 20,
            Y = 40 + index % 8 * 20
        };

        switch (type)
        {
            case HmiWidgetType.Label:
                widget.Text = "Nuovo testo";
                widget.Width = 300;
                widget.Height = 52;
                widget.FontSize = 24;
                widget.Background = "Transparent";
                break;
            case HmiWidgetType.Button:
                widget.Text = "PULSANTE";
                widget.Background = "#227CFF";
                break;
            case HmiWidgetType.ValueDisplay:
                widget.Text = "VALORE";
                widget.Width = 250;
                widget.Height = 140;
                break;
            case HmiWidgetType.Indicator:
                widget.Text = string.Empty;
                widget.Width = 72;
                widget.Height = 72;
                widget.Background = "Transparent";
                widget.TagId = _project.Tags.FirstOrDefault(tag => tag.DataType == TagDataType.Bool && tag.Access != TagAccess.Write)?.Id
                    ?? _project.Tags.FirstOrDefault(IsChartCompatibleTag)?.Id
                    ?? string.Empty;
                widget.Animation.Enabled = true;
                widget.Animation.TagId = widget.TagId;
                widget.Animation.DefaultBackground = "#526273";
                widget.Animation.DefaultForeground = "#8FA0B3";
                widget.Animation.Rules =
                [
                    new HmiAnimationRuleDefinition
                    {
                        Name = "Stato attivo",
                        Condition = HmiDynamicCondition.True,
                        CompareValue = "true",
                        Background = "#22C78A",
                        Foreground = "#F8FAFC"
                    }
                ];
                widget.Animation.Condition = AlarmCondition.True;
                widget.Animation.ActiveBackground = "#22C78A";
                widget.Animation.InactiveBackground = "#526273";
                break;
            case HmiWidgetType.NumericInput:
                widget.Text = "VALORE DA IMPOSTARE";
                widget.Width = 300;
                widget.Height = 112;
                break;
            case HmiWidgetType.Navigation:
                widget.Text = "VAI ALLA PAGINA  →";
                widget.Width = 250;
                widget.Background = "#334155";
                widget.TargetPageId = _project.Pages.FirstOrDefault(page => page.Id != _selectedPage?.Id)?.Id ?? string.Empty;
                break;
            case HmiWidgetType.RecipeManager:
                widget.Text = "GESTIONE RICETTE";
                widget.Width = 560;
                widget.Height = 330;
                widget.Background = "#17212C";
                widget.RecipeBookId = _project.RecipeBooks.FirstOrDefault()?.Id ?? string.Empty;
                break;
            case HmiWidgetType.AlarmViewer:
                widget.Text = "ALLARMI ATTIVI";
                widget.Width = 620;
                widget.Height = 250;
                widget.Background = "#17212C";
                break;
            case HmiWidgetType.AlarmHistoryViewer:
                widget.Text = "STORICO ALLARMI";
                widget.Width = 820;
                widget.Height = 380;
                widget.Background = "#17212C";
                widget.AlarmHistoryRetentionDays = 90;
                break;
            case HmiWidgetType.DataHistoryViewer:
                widget.Text = "STORICO DATI";
                widget.Width = 900;
                widget.Height = 440;
                widget.Background = "#17212C";
                widget.HistoryDatabaseName = _project.Database.DatabaseName;
                widget.HistoryTableName = _project.Database.TableName;
                break;
            case HmiWidgetType.TrendChart:
                widget.Text = "GRAFICO LINEA";
                widget.Width = 700;
                widget.Height = 360;
                widget.Background = "#17212C";
                widget.Foreground = "#28C2B8";
                widget.TagId = _project.Tags.FirstOrDefault(IsChartCompatibleTag)?.Id
                    ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(widget.TagId))
                {
                    var defaultTag = _project.Tags.First(tag => tag.Id == widget.TagId);
                    widget.ChartSeries.Add(new ChartSeriesDefinition
                    {
                        TagId = defaultTag.Id,
                        DisplayName = defaultTag.Name,
                        Color = "#28C2B8"
                    });
                }
                widget.HistoryDatabaseName = _project.Database.DatabaseName;
                widget.HistoryTableName = _project.Database.TableName;
                break;
            case HmiWidgetType.Image:
                widget.Text = "Immagine";
                widget.Width = 320;
                widget.Height = 220;
                widget.Background = "Transparent";
                break;
            case HmiWidgetType.PopupButton:
                widget.Text = "APRI / CHIUDI POPUP";
                widget.Width = 250;
                widget.Background = "#7C5CFC";
                widget.TargetPageId = _project.Pages.FirstOrDefault(page => page.Type == HmiPageType.Popup)?.Id ?? string.Empty;
                break;
            case HmiWidgetType.PopupClose:
                widget.Text = "CHIUDI";
                widget.Width = 130;
                widget.Background = "#3A2026";
                break;
            case HmiWidgetType.RuntimeExit:
                widget.Text = "ESCI RUNTIME";
                widget.Width = 180;
                widget.Background = "#3A2026";
                break;
            case HmiWidgetType.UserManager:
                widget.Text = "GESTIONE UTENTI";
                widget.Width = 820;
                widget.Height = 470;
                widget.Background = "#17212C";
                widget.RequiredAccessLevel = _project.Security.MaximumAccessLevel;
                break;
            case HmiWidgetType.LoginButton:
                widget.Text = "LOGIN";
                widget.Width = 180;
                widget.Height = 64;
                widget.Background = "#227CFF";
                widget.RequiredAccessLevel = 0;
                widget.TextAlignment = HmiTextAlignment.Center;
                break;
            case HmiWidgetType.LogoutButton:
                widget.Text = "LOGOUT";
                widget.Width = 180;
                widget.Height = 64;
                widget.Background = "#334155";
                widget.RequiredAccessLevel = 0;
                widget.TextAlignment = HmiTextAlignment.Center;
                break;
        }
        if (type != HmiWidgetType.Indicator)
        {
            widget.Animation.DefaultBackground = widget.Background;
            widget.Animation.DefaultForeground = widget.Foreground;
        }
        return widget;
    }

    private void DeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPage is null || _selectedWidget is null)
        {
            return;
        }
        var selected = GetSelectedWidgets();
        if (selected.Count == 0)
        {
            selected.Add(_selectedWidget);
        }
        foreach (var widget in selected)
        {
            _selectedPage.Widgets.Remove(widget);
        }
        ClearWidgetSelection();
        MarkDirty();
        RenderDesigner();
        ShowWidgetInspector();
        StatusText.Text = selected.Count == 1 ? "Oggetto eliminato" : $"{selected.Count} oggetti eliminati";
    }

    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Ask(this, "Nuova pagina", "Nome della pagina", $"Pagina {_project.Pages.Count + 1}");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var page = new HmiPageDefinition
        {
            Name = name.Trim(),
            FolderId = _selectedPageFolder?.Id ?? string.Empty,
            Width = _selectedPage?.Width ?? _project.CanvasWidth,
            Height = _selectedPage?.Height ?? _project.CanvasHeight
        };
        _project.Pages.Add(page);
        _selectedPage = page;
        ClearWidgetSelection();
        _pageFolderInspectorActive = false;
        MarkDirty();
        RefreshCollections();
        RenderDesigner();
    }

    private void DeletePage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPage is null || _project.Pages.Count(page => page.Type == HmiPageType.Standard) <= 1 && _selectedPage.Type == HmiPageType.Standard)
        {
            MessageBox.Show("Il progetto deve contenere almeno una pagina.", "HMI Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Eliminare la pagina '{_selectedPage.Name}'?", "Conferma eliminazione", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var removedId = _selectedPage.Id;
        _project.Pages.Remove(_selectedPage);
        foreach (var widget in _project.Pages.SelectMany(page => page.Widgets).Where(widget => widget.TargetPageId == removedId))
        {
            widget.TargetPageId = string.Empty;
        }
        foreach (var page in _project.Pages.Where(page => page.TemplatePageId == removedId))
        {
            page.TemplatePageId = string.Empty;
        }
        if (_project.StartupPageId == removedId)
        {
            _project.StartupPageId = _project.Pages.First(page => page.Type == HmiPageType.Standard).Id;
        }
        _selectedPage = _project.Pages.FirstOrDefault(page => page.Type == HmiPageType.Standard) ?? _project.Pages[0];
        ClearWidgetSelection();
        _pageFolderInspectorActive = false;
        MarkDirty();
        RefreshCollections();
        RenderDesigner();
    }

    private void PageTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (PageTree.SelectedItem is not TreeViewItem item)
        {
            return;
        }
        if (item.Tag is PageFolderDefinition folder)
        {
            if (_pageFolderInspectorActive && folder == _selectedPageFolder)
            {
                return;
            }
            ActivatePageFolderInspector(folder);
            return;
        }
        if (item.Tag is not HmiPageDefinition page || page == _selectedPage && !_pageFolderInspectorActive)
        {
            return;
        }
        ActivatePageInspector(page);
    }

    private void PageTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.Tag is HmiPageDefinition page && page == _selectedPage)
        {
            ActivatePageInspector(page);
        }
        else if (item?.Tag is PageFolderDefinition folder && folder == _selectedPageFolder && _pageFolderInspectorActive)
        {
            ActivatePageFolderInspector(folder);
        }
    }

    private void ActivatePageInspector(HmiPageDefinition page)
    {
        var pageChanged = page != _selectedPage;
        ApplyWidgetInspector();
        _selectedPage = page;
        _selectedPageFolder = _project.PageFolders.FirstOrDefault(folder => folder.Id == page.FolderId);
        ClearWidgetSelection();
        _pageFolderInspectorActive = false;
        if (pageChanged)
        {
            RenderDesigner();
        }
        else
        {
            UpdateDesignerSelection();
        }
        ShowWidgetInspector();
        PopulatePageInspector();
    }

    private void ActivatePageFolderInspector(PageFolderDefinition folder)
    {
        ApplyWidgetInspector();
        _selectedPageFolder = folder;
        ClearWidgetSelection();
        _pageFolderInspectorActive = true;
        UpdateDesignerSelection();
        ShowWidgetInspector();
        PopulatePageFolderInspector();
    }

    private void PageTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PageTree.SelectedItem is TreeViewItem { Tag: PageFolderDefinition })
        {
            RenameSelectedPageFolder();
            e.Handled = true;
            return;
        }
        if (PageTree.SelectedItem is not TreeViewItem { Tag: HmiPageDefinition page })
        {
            return;
        }
        var name = TextPromptWindow.Ask(this, "Rinomina pagina", "Nome della pagina", page.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        page.Name = name.Trim();
        MarkDirty();
        RefreshCollections();
        RenderDesigner();
    }

    private void RefreshPageTree(string? selectedPageId = null, string? selectedFolderId = null)
    {
        PageTree.Items.Clear();
        foreach (var folder in _project.PageFolders.Where(folder => string.IsNullOrWhiteSpace(folder.ParentFolderId)).OrderBy(folder => folder.Name))
        {
            PageTree.Items.Add(CreatePageFolderTreeItem(folder, selectedPageId, selectedFolderId));
        }
        foreach (var page in _project.Pages.Where(page => string.IsNullOrWhiteSpace(page.FolderId)).OrderBy(page => page.Type).ThenBy(page => page.Name))
        {
            PageTree.Items.Add(CreatePageTreeItem(page, selectedPageId));
        }
    }

    private TreeViewItem CreatePageFolderTreeItem(PageFolderDefinition folder, string? selectedPageId, string? selectedFolderId)
    {
        var item = new TreeViewItem
        {
            Header = CreateTreeHeader("▰", folder.Name, "Cartella pagine", "#F1B24A"),
            Tag = folder,
            IsExpanded = true,
            IsSelected = folder.Id == selectedFolderId,
            Foreground = BrushOf("#E8EEF5")
        };
        foreach (var child in _project.PageFolders.Where(candidate => candidate.ParentFolderId == folder.Id).OrderBy(candidate => candidate.Name))
        {
            item.Items.Add(CreatePageFolderTreeItem(child, selectedPageId, selectedFolderId));
        }
        foreach (var page in _project.Pages.Where(candidate => candidate.FolderId == folder.Id).OrderBy(candidate => candidate.Type).ThenBy(candidate => candidate.Name))
        {
            item.Items.Add(CreatePageTreeItem(page, selectedPageId));
        }
        return item;
    }

    private TreeViewItem CreatePageTreeItem(HmiPageDefinition page, string? selectedPageId)
    {
        var (icon, color) = page.Type switch
        {
            HmiPageType.Template => ("T", "#7C5CFC"),
            HmiPageType.Popup => ("▣", "#F1B24A"),
            _ => ("▧", "#28C2B8")
        };
        return new TreeViewItem
        {
            Header = CreateTreeHeader(icon, page.Name, page.Type.ToString(), color),
            Tag = page,
            IsSelected = page.Id == selectedPageId,
            Foreground = BrushOf("#E8EEF5")
        };
    }

    private List<FolderChoice> BuildPageFolderChoices()
    {
        var result = new List<FolderChoice> { new(string.Empty, "— Nessuna cartella —") };
        void Add(string parentId, string prefix)
        {
            foreach (var folder in _project.PageFolders.Where(folder => folder.ParentFolderId == parentId).OrderBy(folder => folder.Name))
            {
                result.Add(new FolderChoice(folder.Id, prefix + folder.Name));
                Add(folder.Id, prefix + folder.Name + " / ");
            }
        }
        Add(string.Empty, string.Empty);
        return result;
    }

    private void AddPageFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Ask(this, "Nuova cartella pagine", "Nome della cartella", "Nuova cartella");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var folder = new PageFolderDefinition
        {
            Name = name.Trim(),
            ParentFolderId = _pageFolderInspectorActive ? _selectedPageFolder?.Id ?? string.Empty : string.Empty
        };
        _project.PageFolders.Add(folder);
        _selectedPageFolder = folder;
        _pageFolderInspectorActive = true;
        MarkDirty();
        RefreshCollections();
    }

    private void RenamePageFolder_Click(object sender, RoutedEventArgs e) => RenameSelectedPageFolder();

    private void RenameSelectedPageFolder()
    {
        if (!_pageFolderInspectorActive || PageTree.SelectedItem is not TreeViewItem { Tag: PageFolderDefinition selectedFolder })
        {
            MessageBox.Show("Selezionare prima una cartella nell'albero delle pagine.", "Cartelle pagine", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedPageFolder = selectedFolder;
        var name = TextPromptWindow.Ask(this, "Rinomina cartella pagine", "Nuovo nome", _selectedPageFolder.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        if (HasSiblingPageFolderWithName(_selectedPageFolder, name.Trim()))
        {
            MessageBox.Show("Esiste già una cartella con questo nome allo stesso livello.", "Cartelle pagine", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedPageFolder.Name = name.Trim();
        _pageFolderInspectorActive = true;
        MarkDirty();
        RefreshCollections();
    }

    private void DeletePageFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_pageFolderInspectorActive || PageTree.SelectedItem is not TreeViewItem { Tag: PageFolderDefinition selectedFolder })
        {
            MessageBox.Show("Selezionare prima una cartella nell'albero delle pagine.", "Cartelle pagine", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedPageFolder = selectedFolder;
        if (MessageBox.Show($"Eliminare la cartella '{_selectedPageFolder.Name}'? Le pagine verranno spostate al livello superiore.",
            "Cartelle pagine", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var parentId = _selectedPageFolder.ParentFolderId;
        foreach (var page in _project.Pages.Where(page => page.FolderId == _selectedPageFolder.Id))
        {
            page.FolderId = parentId;
        }
        foreach (var folder in _project.PageFolders.Where(folder => folder.ParentFolderId == _selectedPageFolder.Id))
        {
            folder.ParentFolderId = parentId;
        }
        _project.PageFolders.Remove(_selectedPageFolder);
        _selectedPageFolder = _project.PageFolders.FirstOrDefault(folder => folder.Id == parentId);
        _pageFolderInspectorActive = _selectedPageFolder is not null;
        MarkDirty();
        RefreshCollections();
    }

    private void DesignCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_runtimeMode || e.OriginalSource != DesignCanvas)
        {
            return;
        }
        ApplyWidgetInspector();
        ClearWidgetSelection();
        _pageFolderInspectorActive = false;
        UpdateDesignerSelection();
        ShowWidgetInspector();
        PopulatePageInspector();
    }

    private void PopulatePageInspector()
    {
        if (_selectedPage is null)
        {
            return;
        }
        _updatingInspector = true;
        PageNameBox.Text = _selectedPage.Name;
        PageTypeCombo.SelectedItem = _selectedPage.Type;
        PageFolderCombo.SelectedValue = _selectedPage.FolderId;
        PageTemplateCombo.SelectedValue = _selectedPage.TemplatePageId;
        PageTemplateCombo.IsEnabled = _selectedPage.Type != HmiPageType.Template;
        PageWidthBox.Text = FormatNumber(_selectedPage.Width);
        PageHeightBox.Text = FormatNumber(_selectedPage.Height);
        PageBackgroundBox.Text = _selectedPage.Background;
        PageStartupCheck.IsChecked = _project.StartupPageId == _selectedPage.Id;
        PageStartupCheck.IsEnabled = _selectedPage.Type == HmiPageType.Standard;
        _updatingInspector = false;
    }

    private void PopulatePageFolderInspector()
    {
        if (_selectedPageFolder is null)
        {
            return;
        }
        _updatingInspector = true;
        PageFolderNameBox.Text = _selectedPageFolder.Name;
        PageFolderParentCombo.ItemsSource = BuildPageFolderParentChoices(_selectedPageFolder);
        PageFolderParentCombo.SelectedValue = _selectedPageFolder.ParentFolderId;
        _updatingInspector = false;
    }

    private List<FolderChoice> BuildPageFolderParentChoices(PageFolderDefinition selectedFolder)
    {
        var excluded = new HashSet<string> { selectedFolder.Id };
        void AddDescendants(string parentId)
        {
            foreach (var child in _project.PageFolders.Where(folder => folder.ParentFolderId == parentId))
            {
                if (excluded.Add(child.Id))
                {
                    AddDescendants(child.Id);
                }
            }
        }
        AddDescendants(selectedFolder.Id);

        var result = new List<FolderChoice> { new(string.Empty, "— Livello principale —") };
        void AddChoices(string parentId, string prefix)
        {
            foreach (var folder in _project.PageFolders.Where(folder => folder.ParentFolderId == parentId && !excluded.Contains(folder.Id)).OrderBy(folder => folder.Name))
            {
                result.Add(new FolderChoice(folder.Id, prefix + folder.Name));
                AddChoices(folder.Id, prefix + folder.Name + " / ");
            }
        }
        AddChoices(string.Empty, string.Empty);
        return result;
    }

    private void ApplyPageFolderProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPageFolder is null)
        {
            return;
        }
        var name = PageFolderNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Il nome della cartella è obbligatorio.", "Cartelle pagine", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var parentId = PageFolderParentCombo.SelectedValue as string ?? string.Empty;
        if (parentId == _selectedPageFolder.Id || IsPageFolderDescendant(parentId, _selectedPageFolder.Id))
        {
            MessageBox.Show("Una cartella non può essere spostata dentro sé stessa o una propria sottocartella.", "Cartelle pagine", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_project.PageFolders.Any(folder => folder.Id != _selectedPageFolder.Id && folder.ParentFolderId == parentId && folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Esiste già una cartella con questo nome allo stesso livello.", "Cartelle pagine", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedPageFolder.Name = name;
        _selectedPageFolder.ParentFolderId = parentId;
        _pageFolderInspectorActive = true;
        MarkDirty();
        RefreshCollections();
        StatusText.Text = "Proprietà cartella pagine aggiornate";
    }

    private bool HasSiblingPageFolderWithName(PageFolderDefinition folder, string name) =>
        _project.PageFolders.Any(candidate => candidate.Id != folder.Id && candidate.ParentFolderId == folder.ParentFolderId && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private bool IsPageFolderDescendant(string possibleDescendantId, string ancestorId)
    {
        var current = _project.PageFolders.FirstOrDefault(folder => folder.Id == possibleDescendantId);
        while (current is not null)
        {
            if (current.Id == ancestorId)
            {
                return true;
            }
            current = _project.PageFolders.FirstOrDefault(folder => folder.Id == current.ParentFolderId);
        }
        return false;
    }

    private void ApplyPageProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPage is null)
        {
            return;
        }
        _selectedPage.Name = string.IsNullOrWhiteSpace(PageNameBox.Text) ? _selectedPage.Name : PageNameBox.Text.Trim();
        _selectedPage.Type = PageTypeCombo.SelectedItem is HmiPageType type ? type : HmiPageType.Standard;
        _selectedPage.FolderId = PageFolderCombo.SelectedValue as string ?? string.Empty;
        _selectedPage.TemplatePageId = _selectedPage.Type == HmiPageType.Template ? string.Empty : PageTemplateCombo.SelectedValue as string ?? string.Empty;
        _selectedPage.Width = Clamp(ParseDouble(PageWidthBox.Text, _selectedPage.Width), 160, 10000);
        _selectedPage.Height = Clamp(ParseDouble(PageHeightBox.Text, _selectedPage.Height), 120, 10000);
        _selectedPage.Background = PageBackgroundBox.Text.Trim();
        if (PageStartupCheck.IsChecked == true && _selectedPage.Type == HmiPageType.Standard)
        {
            _project.StartupPageId = _selectedPage.Id;
        }
        else if (_project.StartupPageId == _selectedPage.Id && _selectedPage.Type != HmiPageType.Standard)
        {
            _project.StartupPageId = _project.Pages.FirstOrDefault(page => page.Type == HmiPageType.Standard && page.Id != _selectedPage.Id)?.Id
                ?? string.Empty;
            _project.Normalize();
        }
        _project.CanvasWidth = _selectedPage.Width;
        _project.CanvasHeight = _selectedPage.Height;
        foreach (var widget in _selectedPage.Widgets)
        {
            widget.X = Clamp(widget.X, 0, Math.Max(0, _selectedPage.Width - widget.Width));
            widget.Y = Clamp(widget.Y, 0, Math.Max(0, _selectedPage.Height - widget.Height));
        }
        _selectedPageFolder = _project.PageFolders.FirstOrDefault(folder => folder.Id == _selectedPage.FolderId);
        _pageFolderInspectorActive = false;
        MarkDirty();
        RefreshCollections();
        RenderDesigner();
        ShowWidgetInspector();
    }

    private void PageBackgroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            PageBackgroundBox.Text = color;
        }
    }

    private async void EnterRuntimeMode_Click(object sender, RoutedEventArgs e)
    {
        await StartRuntimeAsync();
    }

    private async Task StartRuntimeAsync()
    {
        if (_runtimeMode)
        {
            return;
        }

        ApplyWidgetInspector();
        _project.Normalize();
        if (!ValidateRuntimeSecurityConfiguration())
        {
            if (_runtimeOnly)
            {
                _allowRuntimeClose = true;
                Application.Current.Shutdown();
            }
            return;
        }
        var startupPage = _project.Pages.FirstOrDefault(page => page.Id == _project.StartupPageId) ?? _project.Pages.FirstOrDefault(page => page.Type == HmiPageType.Standard);
        if (!_runtimeOnly && startupPage is not null && !PageHasRuntimeExit(startupPage))
        {
            MessageBox.Show("Prima di avviare il runtime inserire un pulsante 'Esci runtime' nella pagina iniziale o nel suo template. Il runtime a schermo intero non può essere chiuso in altro modo.",
                "Sicurezza runtime", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _activeAlarms.Clear();
        var alarmHistoryRetentionDays = _project.Pages
            .SelectMany(page => page.Widgets)
            .Where(widget => widget.Type == HmiWidgetType.AlarmHistoryViewer)
            .Select(widget => widget.AlarmHistoryRetentionDays)
            .DefaultIfEmpty(90)
            .Max();
        try
        {
            await _alarmHistory.InitializeAsync(
                AlarmHistoryService.BuildFilePath(_projectPath, _project.Name),
                alarmHistoryRetentionDays);
        }
        catch (Exception)
        {
            // Se la cartella del progetto esportato non è scrivibile, conserva lo storico nel profilo utente.
            await _alarmHistory.InitializeAsync(
                AlarmHistoryService.BuildFilePath(null, _project.Name),
                alarmHistoryRetentionDays);
        }
        foreach (var entry in _alarmHistory.GetEntries()
                     .Where(entry => entry.ResolvedAtUtc is null && _project.Alarms.Any(alarm => alarm.Id == entry.AlarmId))
                     .GroupBy(entry => entry.AlarmId)
                     .Select(group => group.OrderByDescending(entry => entry.ActivatedAtUtc).First()))
        {
            _activeAlarms[entry.AlarmId] = new AlarmRuntimeState
            {
                OccurrenceId = entry.Id,
                ActivatedAtUtc = entry.ActivatedAtUtc,
                IsAcknowledged = entry.AcknowledgedAtUtc is not null
            };
        }

        await InitializeRuntimeUserSecurityAsync();

        _runtimeMode = true;
        _allowRuntimeClose = false;
        _runtimeExitInProgress = false;
        HideEditorSidebarsForRuntime();
        TopBarRow.Height = new GridLength(0);
        BottomBarRow.Height = new GridLength(0);
        WorkspaceToolbarRow.Height = new GridLength(0);
        TopBar.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Collapsed;
        DesignerToolbar.Visibility = Visibility.Collapsed;
        RuntimeNavigation.Visibility = Visibility.Collapsed;
        WorkspaceScroll.Padding = new Thickness(0);
        WorkspaceScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        WorkspaceScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        CanvasViewbox.Stretch = Stretch.Uniform;
        WorkspaceScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        WorkspaceScroll.VerticalContentAlignment = VerticalAlignment.Stretch;
        CanvasShell.BorderThickness = new Thickness(0);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        _ = Dispatcher.InvokeAsync(UpdateRuntimeViewport);
        DesignModeButton.Background = BrushOf("#182431");
        DesignModeButton.Foreground = BrushOf("#E8EEF5");
        RuntimeModeButton.Background = BrushOf("#28C2B8");
        RuntimeModeButton.Foreground = BrushOf("#061514");
        StatusText.Text = "Avvio runtime e collegamento PLC…";
        ConnectionDot.Fill = BrushOf("#F1B24A");
        RenderRuntimePage(_project.StartupPageId);
        if (_runtimeOnly)
        {
            // Il runtime esportato resta trasparente finché l'editor non è stato completamente sostituito.
            Opacity = 1;
        }
        if (_project.Security.Enabled && _project.Security.RequireLoginAtStartup)
        {
            if (!await RequestRuntimeLoginAsync(startupRequest: true))
            {
                await RequestRuntimeExitAsync();
                return;
            }
        }

        try
        {
            var connected = await _runtime.StartAsync(_project);
            ConnectionDot.Fill = BrushOf(connected ? "#22C78A" : "#EF5B5B");
            StatusText.Text = connected
                ? "Runtime attivo · PLC collegati"
                : "Runtime attivo · uno o più PLC non raggiungibili";
        }
        catch (Exception ex)
        {
            ConnectionDot.Fill = BrushOf("#EF5B5B");
            StatusText.Text = "Runtime attivo · collegamento PLC non disponibile";
            MessageBox.Show(ex.Message, "Errore di connessione", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void EnterDesignMode_Click(object sender, RoutedEventArgs e)
    {
        if (!_runtimeMode || _runtimeOnly)
        {
            return;
        }

        await ReturnToDesignModeAsync();
    }

    private async Task ReturnToDesignModeAsync()
    {
        if (!_runtimeMode || _runtimeOnly)
        {
            _runtimeExitInProgress = false;
            return;
        }
        foreach (var popup in _runtimePopups.ToList())
        {
            popup.Close();
        }
        await LogoutCurrentUserAsync(UserSessionEndReason.ReturnToDevelopment, refreshRuntime: false);
        _userSessionTimer.Stop();
        Exception? stopError = null;
        try
        {
            await _runtime.StopAsync();
        }
        catch (Exception ex)
        {
            stopError = ex;
        }
        try
        {
            await _alarmHistory.FlushAsync();
        }
        catch (Exception ex)
        {
            stopError ??= ex;
        }
        try
        {
            await _userSessionAudit.FlushAsync();
        }
        catch (Exception ex)
        {
            stopError ??= ex;
        }
        _runtimeMode = false;
        _allowRuntimeClose = false;
        _runtimeExitInProgress = false;
        _runtimeBindings.Clear();
        _displayedRuntimeWidgets.Clear();
        RestoreEditorSidebarsAfterRuntime();
        TopBarRow.Height = new GridLength(68);
        BottomBarRow.Height = new GridLength(30);
        WorkspaceToolbarRow.Height = new GridLength(44);
        TopBar.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;
        DesignerToolbar.Visibility = Visibility.Visible;
        RuntimeNavigation.Visibility = Visibility.Collapsed;
        WorkspaceScroll.Padding = new Thickness(36);
        WorkspaceScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        WorkspaceScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        CanvasViewbox.Stretch = Stretch.None;
        CanvasViewbox.Width = double.NaN;
        CanvasViewbox.Height = double.NaN;
        WorkspaceScroll.HorizontalContentAlignment = HorizontalAlignment.Center;
        WorkspaceScroll.VerticalContentAlignment = VerticalAlignment.Center;
        CanvasShell.BorderThickness = new Thickness(1);
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = WindowState.Normal;
        DesignModeButton.Background = BrushOf("#28C2B8");
        DesignModeButton.Foreground = BrushOf("#061514");
        RuntimeModeButton.Background = BrushOf("#182431");
        RuntimeModeButton.Foreground = BrushOf("#E8EEF5");
        ConnectionDot.Fill = BrushOf("#6B7B8C");
        StatusText.Text = "Modalità sviluppo · progetto pronto";
        _editingUser = _editingUser is null
            ? null
            : _project.Security.Users.FirstOrDefault(user => user.Id == _editingUser.Id);
        RefreshCollections();
        RenderDesigner();
        if (stopError is not null)
        {
            MessageBox.Show(stopError.Message, "Arresto runtime", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateRuntimeViewport()
    {
        if (!_runtimeMode || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        CanvasViewbox.Width = ActualWidth;
        CanvasViewbox.Height = ActualHeight;
    }

    private void Runtime_RedundancyStateChanged(object? sender, RedundancyStateChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ConnectionDot.Fill = BrushOf(e.State switch
            {
                RedundancyRuntimeState.Active => "#22C78A",
                RedundancyRuntimeState.Standby => "#227CFF",
                RedundancyRuntimeState.Failover => "#F1B24A",
                _ => "#EF5B5B"
            });
            StatusText.Text = e.Message;
        });
    }

    private bool ValidateRuntimeSecurityConfiguration()
    {
        if (!_project.Security.Enabled)
        {
            return true;
        }
        if (!HasConfiguredAdministrator())
        {
            MessageBox.Show("La sicurezza è abilitata ma non esiste un amministratore attivo con password. Configurarlo nella scheda UTENTI prima di avviare o esportare il runtime.",
                "Configurazione sicurezza incompleta", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var hasProtectedWidgets = _project.Pages.SelectMany(page => page.Widgets)
            .Any(widget => widget.RequiredAccessLevel > _project.Security.AnonymousAccessLevel || widget.Type == HmiWidgetType.UserManager);
        var startupPage = _project.Pages.FirstOrDefault(page => page.Id == _project.StartupPageId && page.Type == HmiPageType.Standard)
            ?? _project.Pages.FirstOrDefault(page => page.Type == HmiPageType.Standard);
        var hasReachableLoginButton = startupPage is not null &&
            GetRuntimePageWidgets(startupPage).Any(widget => widget.Type == HmiWidgetType.LoginButton);
        if (hasProtectedWidgets && !_project.Security.RequireLoginAtStartup && !hasReachableLoginButton)
        {
            MessageBox.Show("Sono presenti oggetti protetti ma nella pagina iniziale o nel template applicato non è presente un pulsante Login e il login iniziale non è attivo.",
                "Configurazione sicurezza incompleta", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private async Task InitializeRuntimeUserSecurityAsync()
    {
        _currentUser = null;
        _currentUserAuditSessionId = null;
        _lastUserActivityUtc = DateTime.UtcNow;
        var auditPath = UserSessionAuditService.BuildFilePath(_projectPath, _project.Name, _runtimeOnly);
        try
        {
            await _userSessionAudit.InitializeAsync(auditPath, _project.Security.SessionHistoryRetentionDays);
        }
        catch
        {
            var fallbackPath = UserSessionAuditService.BuildFilePath(null, _project.Name, true);
            await _userSessionAudit.InitializeAsync(fallbackPath, _project.Security.SessionHistoryRetentionDays);
        }
        await _userSessionAudit.CloseOpenSessionsAsync(UserSessionEndReason.UnexpectedTermination);
        _userSessionTimer.Start();
    }

    private async Task<bool> RequestRuntimeLoginAsync(bool startupRequest = false)
    {
        if (!_project.Security.Enabled)
        {
            MessageBox.Show("La gestione utenti non è abilitata per questo progetto.", "Login", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        while (true)
        {
            var credentials = LoginWindow.Ask(this, startupRequest ? "Login richiesto" : "Login operatore", _currentUser?.Username ?? string.Empty);
            if (credentials is null)
            {
                return false;
            }
            var result = _userSecurity.Authenticate(_project.Security, credentials.Username, credentials.Password);
            if (!result.Succeeded || result.User is null)
            {
                await CommitRuntimeProjectChangeAsync(
                    "Stato tentativi login aggiornato",
                    persistSecurityOverride: true,
                    showSuccessStatus: false);
                var message = result.FailureReason == AuthenticationFailureReason.AccountLocked && result.LockedUntilUtc is not null
                    ? $"Account temporaneamente bloccato fino alle {result.LockedUntilUtc.Value.ToLocalTime():HH:mm:ss}."
                    : "Nome utente o password non validi.";
                MessageBox.Show(message, "Login non riuscito", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (startupRequest)
                {
                    continue;
                }
                return false;
            }
            if (_currentUser is not null)
            {
                await LogoutCurrentUserAsync(UserSessionEndReason.UserChanged, refreshRuntime: false);
            }
            _currentUser = result.User;
            _lastUserActivityUtc = DateTime.UtcNow;
            try
            {
                var auditEntry = await _userSessionAudit.RecordLoginAsync(result.User, _runtimeOnly ? "Runtime esportato" : "Anteprima sviluppo");
                _currentUserAuditSessionId = auditEntry.Id;
            }
            catch (Exception ex)
            {
                _currentUserAuditSessionId = null;
                StatusText.Text = $"Login eseguito; registro sessioni non disponibile: {ex.Message}";
            }
            if (result.PasswordRehashRecommended)
            {
                var projectUser = _project.Security.Users.FirstOrDefault(user => user.Id == result.User.Id);
                if (projectUser is not null)
                {
                    PasswordHashingService.SetPassword(projectUser, credentials.Password, _project.Security.MinimumPasswordLength);
                }
            }
            await CommitRuntimeProjectChangeAsync(
                "Stato autenticazione aggiornato",
                persistSecurityOverride: true,
                showSuccessStatus: false);
            RefreshRuntimeAccessStates();
            RefreshRuntimeUserManagers();
            StatusText.Text = $"Login: {result.User.DisplayName} · livello {result.User.AccessLevel}";
            return true;
        }
    }

    private async Task LogoutCurrentUserAsync(UserSessionEndReason reason, bool refreshRuntime = true)
    {
        var previousUser = _currentUser;
        var auditSessionId = _currentUserAuditSessionId;
        _currentUser = null;
        _currentUserAuditSessionId = null;
        if (!string.IsNullOrWhiteSpace(auditSessionId))
        {
            try
            {
                await _userSessionAudit.RecordLogoutAsync(auditSessionId, reason);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Logout eseguito; registro sessioni non disponibile: {ex.Message}";
            }
        }
        if (refreshRuntime)
        {
            RefreshRuntimeAccessStates();
            RefreshRuntimeUserManagers();
        }
        if (previousUser is not null)
        {
            StatusText.Text = $"Logout: {previousUser.DisplayName}";
        }
    }

    private bool HasWidgetAccess(HmiWidgetDefinition widget)
    {
        if (!_project.Security.Enabled)
        {
            return true;
        }
        if (widget.Type == HmiWidgetType.LoginButton)
        {
            return true;
        }
        if (widget.Type == HmiWidgetType.LogoutButton)
        {
            return _currentUser is not null;
        }
        return _userSecurity.HasAccess(_currentUser, _project.Security, widget.RequiredAccessLevel);
    }

    private bool EnsureWidgetAccess(HmiWidgetDefinition widget, string operation)
    {
        RecordRuntimeUserActivity();
        if (HasWidgetAccess(widget))
        {
            return true;
        }
        var currentLevel = _currentUser?.AccessLevel ?? _project.Security.AnonymousAccessLevel;
        MessageBox.Show($"Operazione '{operation}' non consentita. Livello richiesto: {widget.RequiredAccessLevel}; livello corrente: {currentLevel}.",
            "Accesso insufficiente", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void RefreshRuntimeAccessStates()
    {
        foreach (var binding in _runtimeBindings.Values.Where(binding => binding.Root is not null && binding.Widget is not null))
        {
            ApplyRuntimeAccessState(binding);
        }
    }

    private void ApplyRuntimeAccessState(RuntimeWidgetBinding binding)
    {
        if (binding.Root is null || binding.Widget is null)
        {
            return;
        }
        var access = HasWidgetAccess(binding.Widget);
        binding.Root.IsEnabled = access;
        binding.Root.Opacity = access ? 1 : 0.38;
        binding.Root.ToolTip = access
            ? null
            : $"Accesso richiesto: livello {binding.Widget.RequiredAccessLevel}";
        ToolTipService.SetShowOnDisabled(binding.Root, true);
        if (binding.UserProtectedContent is not null)
        {
            binding.UserProtectedContent.Visibility = access ? Visibility.Visible : Visibility.Collapsed;
        }
        if (binding.UserAccessDeniedContent is not null)
        {
            binding.UserAccessDeniedContent.Visibility = access ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void RecordRuntimeUserActivity()
    {
        if (_runtimeMode && _currentUser is not null)
        {
            _lastUserActivityUtc = DateTime.UtcNow;
        }
    }

    private async void UserSessionTimer_Tick(object? sender, EventArgs e)
    {
        if (!_runtimeMode || _currentUser is null || _project.Security.AutomaticLogoutMinutes <= 0 ||
            DateTime.UtcNow - _lastUserActivityUtc < TimeSpan.FromMinutes(_project.Security.AutomaticLogoutMinutes))
        {
            return;
        }
        await LogoutCurrentUserAsync(UserSessionEndReason.AutomaticTimeout);
        MessageBox.Show("Sessione terminata per inattività.", "Logout automatico", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RenderRuntimePage(string pageId)
    {
        var page = _project.Pages.FirstOrDefault(item => item.Id == pageId && item.Type != HmiPageType.Template)
            ?? _project.Pages.FirstOrDefault(item => item.Type == HmiPageType.Standard)
            ?? _project.Pages[0];
        if (page.Type == HmiPageType.Popup)
        {
            OpenRuntimePopup(page.Id);
            return;
        }
        foreach (var popup in _runtimePopups.ToList())
        {
            popup.Close();
        }
        _selectedPage = page;
        _runtimeBindings.Clear();
        _displayedRuntimeWidgets.Clear();
        DesignCanvas.Children.Clear();
        DesignCanvas.Width = page.Width;
        DesignCanvas.Height = page.Height;
        DesignCanvas.Background = BrushOf(page.Background, "#101821");

        foreach (var widget in GetRuntimePageWidgets(page))
        {
            AddRuntimeWidgetToCanvas(widget, DesignCanvas);
        }
    }

    private IEnumerable<HmiWidgetDefinition> GetRuntimePageWidgets(HmiPageDefinition page)
    {
        if (page.Type != HmiPageType.Template && !string.IsNullOrWhiteSpace(page.TemplatePageId))
        {
            var template = _project.Pages.FirstOrDefault(item => item.Id == page.TemplatePageId && item.Type == HmiPageType.Template);
            if (template is not null)
            {
                foreach (var widget in template.Widgets)
                {
                    yield return widget;
                }
            }
        }
        foreach (var widget in page.Widgets)
        {
            yield return widget;
        }
    }

    private void AddRuntimeWidgetToCanvas(HmiWidgetDefinition widget, Canvas canvas)
    {
        var visual = CreateWidgetVisual(widget, true);
        visual.Width = widget.Width;
        visual.Height = widget.Height;
        Canvas.SetLeft(visual, widget.X);
        Canvas.SetTop(visual, widget.Y);
        canvas.Children.Add(visual);
        _displayedRuntimeWidgets.Add(widget);
        if (!_runtimeBindings.TryGetValue(widget.Id, out var binding))
        {
            binding = new RuntimeWidgetBinding();
            _runtimeBindings[widget.Id] = binding;
        }
        binding.Widget = widget;
        binding.Root = visual;
        ApplyWidgetAnimationFallback(widget);
        if (widget.Type == HmiWidgetType.TrendChart)
        {
            foreach (var series in widget.ChartSeries)
            {
                var seriesValue = _runtime.GetValue(series.TagId);
                if (seriesValue is not null)
                {
                    AddLiveTrendPoint(widget, series.TagId, seriesValue);
                }
            }
            if (widget.ChartSource == ChartDataSource.LivePlc)
            {
                RenderTrendChart(widget, binding);
            }
        }
        else
        {
            var existing = _runtime.GetValue(widget.TagId);
            if (existing is not null)
            {
                UpdateRuntimeWidget(widget, existing);
            }
        }
        var animationValue = _runtime.GetValue(widget.Animation.TagId);
        if (animationValue is not null)
        {
            ApplyWidgetAnimation(widget, animationValue);
        }
        ApplyRuntimeAccessState(binding);
    }

    private bool PageHasRuntimeExit(HmiPageDefinition page) =>
        GetRuntimePageWidgets(page).Any(widget => widget.Type == HmiWidgetType.RuntimeExit);

    private void ToggleRuntimePopup(string pageId)
    {
        var existingPopup = _runtimePopups.FirstOrDefault(window => string.Equals(window.Tag as string, pageId, StringComparison.Ordinal));
        if (existingPopup is not null)
        {
            existingPopup.Close();
            return;
        }
        OpenRuntimePopup(pageId);
    }

    private void OpenRuntimePopup(string pageId)
    {
        var page = _project.Pages.FirstOrDefault(item => item.Id == pageId && item.Type == HmiPageType.Popup);
        if (page is null)
        {
            return;
        }
        CloseLastRuntimePopup();
        var canvas = new Canvas
        {
            Width = page.Width,
            Height = page.Height,
            Background = BrushOf(page.Background, "#17212C"),
            ClipToBounds = true
        };
        foreach (var widget in page.Widgets)
        {
            AddRuntimeWidgetToCanvas(widget, canvas);
        }
        var popup = new Window
        {
            Owner = this,
            Width = page.Width,
            Height = page.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Brushes.Black,
            Tag = page.Id,
            Content = new Viewbox { Stretch = Stretch.Uniform, Child = canvas }
        };
        popup.PreviewMouseDown += (_, _) => RecordRuntimeUserActivity();
        popup.PreviewKeyDown += (_, _) => RecordRuntimeUserActivity();
        popup.PreviewTouchDown += (_, _) => RecordRuntimeUserActivity();
        popup.Closed += (_, _) =>
        {
            _runtimePopups.Remove(popup);
            foreach (var widget in page.Widgets)
            {
                _displayedRuntimeWidgets.Remove(widget);
                _runtimeBindings.Remove(widget.Id);
            }
        };
        _runtimePopups.Add(popup);
        popup.Show();
    }

    private void CloseLastRuntimePopup()
    {
        if (_runtimePopups.Count > 0)
        {
            _runtimePopups[^1].Close();
        }
    }

    private async Task RequestRuntimeExitAsync()
    {
        if (!_runtimeMode || _runtimeExitInProgress)
        {
            return;
        }
        _runtimeExitInProgress = true;
        foreach (var popup in _runtimePopups.ToList())
        {
            popup.Close();
        }

        if (!_runtimeOnly)
        {
            await ReturnToDesignModeAsync();
            return;
        }

        await LogoutCurrentUserAsync(UserSessionEndReason.RuntimeExit, refreshRuntime: false);
        _userSessionTimer.Stop();

        try
        {
            var stopTask = _runtime.StopAsync();
            if (await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(4))) == stopTask)
            {
                await stopTask;
            }
        }
        catch
        {
            // L'uscita operatore deve chiudere comunque l'applicazione anche se una connessione non risponde.
        }
        try
        {
            await _alarmHistory.FlushAsync();
        }
        catch
        {
            // Anche un supporto di memorizzazione non disponibile non deve bloccare l'uscita del runtime esportato.
        }
        try
        {
            await _userSessionAudit.FlushAsync();
        }
        catch
        {
            // I due archivi sono indipendenti: un errore nello storico allarmi non deve saltare il registro utenti.
        }
        _allowRuntimeClose = true;
        Application.Current.Shutdown();
    }

    private async Task WriteWidgetValueAsync(HmiWidgetDefinition widget, string value)
    {
        if (!EnsureWidgetAccess(widget, "scrittura sul PLC"))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(widget.TagId))
        {
            StatusText.Text = $"'{widget.Name}' non è collegato a una tag";
            return;
        }

        try
        {
            var success = await _runtime.WriteAsync(widget.TagId, value);
            StatusText.Text = success ? $"Valore scritto da '{widget.Name}'" : $"Scrittura non consentita per '{widget.Name}'";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Valore non valido per '{widget.Name}'";
            MessageBox.Show(ex.Message, "Scrittura tag", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Runtime_TagValueChanged(object? sender, TagValueChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_runtimeMode || _selectedPage is null)
            {
                return;
            }
            foreach (var widget in _displayedRuntimeWidgets.Where(item => item.Type != HmiWidgetType.TrendChart && item.TagId == e.TagId).ToList())
            {
                UpdateRuntimeWidget(widget, e.Value);
            }
            foreach (var widget in _displayedRuntimeWidgets.Where(item => item.Type == HmiWidgetType.TrendChart && item.ChartSeries.Any(series => series.TagId == e.TagId)).ToList())
            {
                AddLiveTrendPoint(widget, e.TagId, e.Value);
            }
            foreach (var widget in _displayedRuntimeWidgets.Where(item => item.Animation.Enabled && item.Animation.TagId == e.TagId).ToList())
            {
                ApplyWidgetAnimation(widget, e.Value);
            }
            EvaluateAlarms(e.TagId, e.Value);
            foreach (var binding in _runtimeBindings.Values.Where(binding => binding.RecipeBook?.TagIds.Contains(e.TagId) == true))
            {
                RefreshRecipeBinding(binding);
            }
        });
    }

    private void EvaluateAlarms(string tagId, object value)
    {
        foreach (var alarm in _project.Alarms.Where(alarm => alarm.TagId == tagId))
        {
            var isActive = IsAlarmConditionMet(alarm, value);
            if (isActive)
            {
                if (!_activeAlarms.TryGetValue(alarm.Id, out var existing) || existing.ResolvedAtUtc is not null)
                {
                    var activatedAtUtc = DateTime.UtcNow;
                    var entry = _alarmHistory.RecordActivation(alarm, activatedAtUtc);
                    _activeAlarms[alarm.Id] = new AlarmRuntimeState
                    {
                        OccurrenceId = entry.Id,
                        ActivatedAtUtc = activatedAtUtc
                    };
                }
            }
            else if (_activeAlarms.TryGetValue(alarm.Id, out var state))
            {
                if (state.ResolvedAtUtc is null)
                {
                    state.ResolvedAtUtc = DateTime.UtcNow;
                    _alarmHistory.RecordResolution(state.OccurrenceId, state.ResolvedAtUtc.Value);
                }
                if (!alarm.RequiresAcknowledgement || state.IsAcknowledged)
                {
                    _activeAlarms.Remove(alarm.Id);
                }
            }
        }
        RefreshAlarmBindings();
        RefreshAlarmHistoryBindings();
    }

    private static bool IsAlarmConditionMet(AlarmDefinition alarm, object value) =>
        IsConditionMet(alarm.Condition, alarm.TriggerValue, value);

    private static bool IsConditionMet(AlarmCondition condition, string compareValue, object value)
    {
        var valueText = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var triggerText = compareValue ?? string.Empty;
        var valueBoolean = bool.TryParse(valueText, out var parsedBoolean) ? parsedBoolean : valueText == "1";
        return condition switch
        {
            AlarmCondition.True => valueBoolean,
            AlarmCondition.False => !valueBoolean,
            AlarmCondition.Equals => string.Equals(valueText, triggerText, StringComparison.OrdinalIgnoreCase) || NumericCompare(valueText, triggerText, result => result == 0),
            AlarmCondition.NotEquals => !(string.Equals(valueText, triggerText, StringComparison.OrdinalIgnoreCase) || NumericCompare(valueText, triggerText, result => result == 0)),
            AlarmCondition.GreaterThan => NumericCompare(valueText, triggerText, result => result > 0),
            AlarmCondition.LessThan => NumericCompare(valueText, triggerText, result => result < 0),
            _ => false
        };
    }

    private static bool NumericCompare(string value, string trigger, Func<int, bool> comparison)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) ||
            !double.TryParse(trigger.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
        {
            return false;
        }
        return comparison(left.CompareTo(right));
    }

    private void AcknowledgeAllAlarms()
    {
        var acknowledgedAtUtc = DateTime.UtcNow;
        foreach (var state in _activeAlarms.Values)
        {
            state.IsAcknowledged = true;
            _alarmHistory.RecordAcknowledgement(state.OccurrenceId, acknowledgedAtUtc);
        }
        foreach (var alarmId in _activeAlarms.Where(pair => pair.Value.ResolvedAtUtc is not null).Select(pair => pair.Key).ToList())
        {
            _activeAlarms.Remove(alarmId);
        }
        StatusText.Text = "Allarmi riconosciuti dall'operatore";
        RefreshAlarmBindings();
        RefreshAlarmHistoryBindings();
    }

    private void RefreshAlarmBindings()
    {
        foreach (var binding in _runtimeBindings.Values.Where(binding => binding.AlarmPanel is not null))
        {
            binding.AlarmPanel!.Children.Clear();
            var search = binding.AlarmSearchBox?.Text?.Trim() ?? string.Empty;
            var severityFilter = binding.AlarmSeverityCombo?.SelectedItem as string ?? "Tutte";
            var stateFilter = binding.AlarmStateCombo?.SelectedItem as string ?? "Tutti";
            var active = _project.Alarms.Where(alarm => _activeAlarms.ContainsKey(alarm.Id))
                .Where(alarm => string.IsNullOrWhiteSpace(search) ||
                    alarm.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    alarm.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (_project.AlarmFolders.FirstOrDefault(folder => folder.Id == alarm.FolderId)?.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                .Where(alarm => severityFilter == "Tutte" || alarm.Severity.ToString().Equals(severityFilter, StringComparison.OrdinalIgnoreCase))
                .Where(alarm => stateFilter == "Tutti" ||
                    (stateFilter == "Attivi" && _activeAlarms[alarm.Id].ResolvedAtUtc is null) ||
                    (stateFilter == "Rientrati" && _activeAlarms[alarm.Id].ResolvedAtUtc is not null))
                .OrderByDescending(alarm => alarm.Severity)
                .ThenBy(alarm => _activeAlarms[alarm.Id].ActivatedAtUtc)
                .ToList();
            if (active.Count == 0)
            {
                binding.AlarmPanel.Children.Add(CreateAlarmRow("OK", "Nessun allarme corrispondente ai filtri", "#22C78A", null, null));
                continue;
            }
            foreach (var alarm in active)
            {
                var color = alarm.Severity switch
                {
                    AlarmSeverity.Critical => "#EF5B5B",
                    AlarmSeverity.Warning => "#F1B24A",
                    _ => "#227CFF"
                };
                var runtimeState = _activeAlarms[alarm.Id];
                var state = runtimeState.IsAcknowledged ? "ACK" : runtimeState.ResolvedAtUtc is not null ? "RIENTRATO" : alarm.Severity.ToString().ToUpperInvariant();
                binding.AlarmPanel.Children.Add(CreateAlarmRow(
                    state,
                    alarm.Message,
                    color,
                    runtimeState.ActivatedAtUtc.ToLocalTime(),
                    runtimeState.ResolvedAtUtc?.ToLocalTime()));
            }
        }
    }

    private void RefreshAlarmHistoryBindings()
    {
        var entries = _alarmHistory.GetEntries();
        foreach (var binding in _runtimeBindings.Values.Where(binding => binding.AlarmHistoryPanel is not null))
        {
            binding.AlarmHistoryPanel!.Children.Clear();
            var search = binding.AlarmSearchBox?.Text?.Trim() ?? string.Empty;
            var severityFilter = binding.AlarmSeverityCombo?.SelectedItem as string ?? "Tutte";
            var stateFilter = binding.AlarmStateCombo?.SelectedItem as string ?? "Tutti";
            var fromDate = binding.AlarmFromPicker?.SelectedDate;
            var toDate = binding.AlarmToPicker?.SelectedDate?.AddDays(1);
            var retentionCutoffUtc = DateTime.UtcNow.AddDays(-Math.Clamp(binding.AlarmHistoryRetentionDays, 1, 3650));
            var filtered = entries
                .Where(entry => entry.ActivatedAtUtc >= retentionCutoffUtc || entry.ResolvedAtUtc is null)
                .Where(entry => string.IsNullOrWhiteSpace(search) ||
                    entry.AlarmName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (_project.AlarmFolders.FirstOrDefault(folder => folder.Id == entry.FolderId)?.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                .Where(entry => severityFilter == "Tutte" || entry.Severity.ToString().Equals(severityFilter, StringComparison.OrdinalIgnoreCase))
                .Where(entry => stateFilter == "Tutti" ||
                    (stateFilter == "Aperti" && entry.ResolvedAtUtc is null) ||
                    (stateFilter == "Risolti" && entry.ResolvedAtUtc is not null))
                .Where(entry => fromDate is null || entry.ActivatedAtUtc.ToLocalTime() >= fromDate.Value)
                .Where(entry => toDate is null || entry.ActivatedAtUtc.ToLocalTime() < toDate.Value)
                .OrderByDescending(entry => entry.ActivatedAtUtc)
                .Take(2000)
                .ToList();
            if (filtered.Count == 0)
            {
                binding.AlarmHistoryPanel.Children.Add(CreateAlarmRow("STORICO", "Nessun allarme corrispondente ai filtri", "#526273", null, null));
                continue;
            }
            foreach (var entry in filtered)
            {
                var color = entry.Severity switch
                {
                    AlarmSeverity.Critical => "#EF5B5B",
                    AlarmSeverity.Warning => "#F1B24A",
                    _ => "#227CFF"
                };
                binding.AlarmHistoryPanel.Children.Add(CreateAlarmRow(
                    entry.ResolvedAtUtc is null ? "APERTA" : "RISOLTA",
                    $"{entry.AlarmName} · {entry.Message}",
                    color,
                    entry.ActivatedAtUtc.ToLocalTime(),
                    entry.ResolvedAtUtc?.ToLocalTime()));
            }
        }
    }

    private static FrameworkElement CreateAlarmRow(string state, string message, string color, DateTime? activatedAt, DateTime? resolvedAt)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = BrushOf("#101923") };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var severity = new Border
        {
            Background = BrushOf(color),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(4),
            Child = new TextBlock { Text = state, Foreground = BrushOf("#071018"), FontSize = 9, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center }
        };
        row.Children.Add(severity);
        var description = new TextBlock { Text = message, Foreground = BrushOf("#E8EEF5"), Margin = new Thickness(8, 7, 8, 7), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(description, 1);
        row.Children.Add(description);
        var activation = new TextBlock { Text = "Attivazione\n" + (activatedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "—"), Foreground = BrushOf("#8FA0B3"), Margin = new Thickness(8), FontSize = 10 };
        Grid.SetColumn(activation, 2);
        row.Children.Add(activation);
        var resolution = new TextBlock { Text = "Risoluzione\n" + (resolvedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "—"), Foreground = BrushOf("#8FA0B3"), Margin = new Thickness(8), FontSize = 10 };
        Grid.SetColumn(resolution, 3);
        row.Children.Add(resolution);
        return row;
    }

    private void UpdateRuntimeWidget(HmiWidgetDefinition widget, object value)
    {
        if (!_runtimeBindings.TryGetValue(widget.Id, out var binding))
        {
            return;
        }

        if (binding.ValueText is not null)
        {
            binding.ValueText.Text = FormatTagValue(value, widget.Decimals) + widget.Suffix;
        }
        if (binding.Input is not null && !binding.Input.IsKeyboardFocusWithin)
        {
            binding.Input.Text = FormatTagValue(value, widget.Decimals);
        }
    }

    private void ApplyWidgetAnimation(HmiWidgetDefinition widget, object value)
    {
        if (!widget.Animation.Enabled || !_runtimeBindings.TryGetValue(widget.Id, out var binding) || binding.Root is null)
        {
            return;
        }
        var sourceTag = GetAnimationSourceTag(widget);
        var sourceDataType = sourceTag?.DataType;
        var matchingRule = widget.Animation.Rules.FirstOrDefault(rule =>
            rule.Enabled && IsDynamicConditionCompatible(sourceDataType, rule.Condition) &&
            TryValidateDynamicRule(rule, sourceTag, out _) &&
            IsDynamicConditionMet(rule, value, sourceDataType));
        ApplyDynamicAppearance(widget, binding, matchingRule);
    }

    private void ApplyWidgetAnimationFallback(HmiWidgetDefinition widget)
    {
        if (!widget.Animation.Enabled || !_runtimeBindings.TryGetValue(widget.Id, out var binding) || binding.Root is null)
        {
            return;
        }
        ApplyDynamicAppearance(widget, binding, null);
    }

    private static void ApplyDynamicAppearance(HmiWidgetDefinition widget, RuntimeWidgetBinding binding, HmiAnimationRuleDefinition? matchingRule)
    {
        var root = binding.Root;
        if (root is null)
        {
            return;
        }
        if (binding.Indicator is not null)
        {
            var indicatorColor = ResolveDynamicColor(
                matchingRule?.Background, widget.Animation.DefaultBackground, null, "#526273");
            ApplyIndicatorColor(binding.Indicator, indicatorColor, matchingRule is not null);
            return;
        }
        var backgroundCode = ResolveDynamicColor(
            matchingRule?.Background, widget.Animation.DefaultBackground, widget.Background, "#253244");
        var foregroundCode = ResolveDynamicColor(
            matchingRule?.Foreground, widget.Animation.DefaultForeground, widget.Foreground, "#F8FAFC");
        var hideNumericBackground = (widget.Type is HmiWidgetType.ValueDisplay or HmiWidgetType.NumericInput) && !widget.ShowBackground;
        var background = hideNumericBackground
            ? Brushes.Transparent
            : BrushOf(backgroundCode, "#253244");
        var foreground = BrushOf(foregroundCode, "#F8FAFC");
        ApplyDynamicColors(root, background, foreground);
    }

    private static string ResolveDynamicColor(string? ruleColor, string? defaultColor, string? staticColor, string finalFallback)
    {
        if (IsValidBrushCode(ruleColor))
        {
            return ruleColor!.Trim();
        }
        if (IsValidBrushCode(defaultColor))
        {
            return defaultColor!.Trim();
        }
        return IsValidBrushCode(staticColor) ? staticColor!.Trim() : finalFallback;
    }

    private static void ApplyIndicatorColor(Ellipse indicator, string? colorCode, bool matched)
    {
        var color = string.IsNullOrWhiteSpace(colorCode) ? "#526273" : colorCode;
        indicator.Fill = BrushOf(color, "#526273");
        indicator.Stroke = BrushOf(color, "#718196");
        indicator.Opacity = matched ? 1 : 0.88;
    }

    private static bool IsDynamicConditionMet(HmiAnimationRuleDefinition rule, object value, TagDataType? sourceDataType)
    {
        var valueText = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var hasBooleanValue = TryConvertDynamicBoolean(valueText, out var valueBoolean);
        return rule.Condition switch
        {
            HmiDynamicCondition.True => hasBooleanValue && valueBoolean,
            HmiDynamicCondition.False => hasBooleanValue && !valueBoolean,
            HmiDynamicCondition.Equals => DynamicValuesEqual(valueText, rule.CompareValue, sourceDataType),
            HmiDynamicCondition.NotEquals => !DynamicValuesEqual(valueText, rule.CompareValue, sourceDataType),
            HmiDynamicCondition.GreaterThan => DynamicNumericCompare(valueText, rule.CompareValue, result => result > 0),
            HmiDynamicCondition.GreaterThanOrEqual => DynamicNumericCompare(valueText, rule.CompareValue, result => result >= 0),
            HmiDynamicCondition.LessThan => DynamicNumericCompare(valueText, rule.CompareValue, result => result < 0),
            HmiDynamicCondition.LessThanOrEqual => DynamicNumericCompare(valueText, rule.CompareValue, result => result <= 0),
            HmiDynamicCondition.BetweenInclusive => TryParseDynamicNumber(valueText, out var number) &&
                TryParseDynamicNumber(rule.CompareValue, out var minimum) &&
                TryParseDynamicNumber(rule.CompareValue2, out var maximum) &&
                minimum <= maximum && number >= minimum && number <= maximum,
            HmiDynamicCondition.BitSet => TryGetDynamicUnsignedInteger(valueText, sourceDataType, out var setValue) &&
                int.TryParse(rule.CompareValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var setBit) &&
                setBit >= 0 && setBit <= GetDynamicMaximumBit(sourceDataType) && (setValue & (1UL << setBit)) != 0,
            HmiDynamicCondition.BitClear => TryGetDynamicUnsignedInteger(valueText, sourceDataType, out var clearValue) &&
                int.TryParse(rule.CompareValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var clearBit) &&
                clearBit >= 0 && clearBit <= GetDynamicMaximumBit(sourceDataType) && (clearValue & (1UL << clearBit)) == 0,
            HmiDynamicCondition.BitMaskEquals => TryGetDynamicUnsignedInteger(valueText, sourceDataType, out var maskedValue) &&
                TryGetDynamicUnsignedInteger(rule.CompareValue, sourceDataType, out var mask) &&
                TryGetDynamicUnsignedInteger(rule.CompareValue2, sourceDataType, out var expected) && (maskedValue & mask) == expected,
            _ => false
        };
    }

    private static bool DynamicValuesEqual(string value, string trigger, TagDataType? sourceDataType)
    {
        if (sourceDataType == TagDataType.String)
        {
            return string.Equals(value, trigger, StringComparison.OrdinalIgnoreCase);
        }
        if (sourceDataType == TagDataType.Bool &&
            TryConvertDynamicBoolean(value, out var leftBoolean) &&
            TryConvertDynamicBoolean(trigger, out var rightBoolean))
        {
            return leftBoolean == rightBoolean;
        }
        return string.Equals(value, trigger, StringComparison.OrdinalIgnoreCase) ||
            DynamicNumericCompare(value, trigger, result => result == 0);
    }

    private static bool TryConvertDynamicBoolean(string? text, out bool value)
    {
        var normalized = (text ?? string.Empty).Trim();
        if (bool.TryParse(normalized, out value))
        {
            return true;
        }
        if (normalized.Equals("vero", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (normalized.Equals("falso", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        if (TryParseDynamicNumber(normalized, out var number) && (number == 0 || number == 1))
        {
            value = number == 1;
            return true;
        }
        value = false;
        return false;
    }

    private static bool DynamicNumericCompare(string value, string trigger, Func<int, bool> comparison)
    {
        if (!TryParseDynamicNumber(value, out var left) || !TryParseDynamicNumber(trigger, out var right))
        {
            return false;
        }
        return comparison(left.CompareTo(right));
    }

    private static bool TryParseDynamicNumber(string? text, out double number)
    {
        var parsed = double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out number);
        return parsed && double.IsFinite(number);
    }

    private static bool TryGetDynamicUnsignedInteger(string? text, TagDataType? dataType, out ulong number)
    {
        number = 0;
        if (!TryParseDynamicInteger(text, out var signedValue))
        {
            return false;
        }
        if (dataType == TagDataType.Int && signedValue >= short.MinValue && signedValue <= ushort.MaxValue)
        {
            number = unchecked((ushort)signedValue);
            return true;
        }
        if (dataType == TagDataType.DInt && signedValue >= int.MinValue && signedValue <= uint.MaxValue)
        {
            number = unchecked((uint)signedValue);
            return true;
        }
        return false;
    }

    private static bool TryParseDynamicInteger(string? text, out long number)
    {
        var normalized = (text ?? string.Empty).Trim();
        if (bool.TryParse(normalized, out var boolean))
        {
            number = boolean ? 1 : 0;
            return true;
        }
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(normalized[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hexadecimal))
        {
            number = unchecked((long)hexadecimal);
            return true;
        }
        if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return true;
        }
        if (TryParseDynamicNumber(normalized, out var floatingPoint) && floatingPoint >= long.MinValue && floatingPoint <= long.MaxValue &&
            floatingPoint == Math.Truncate(floatingPoint))
        {
            number = (long)floatingPoint;
            return true;
        }
        number = 0;
        return false;
    }

    private static void ApplyDynamicColors(DependencyObject element, Brush background, Brush foreground)
    {
        switch (element)
        {
            case Border border:
                border.Background = background;
                break;
            case Control control:
                control.Background = background;
                control.Foreground = foreground;
                break;
            case TextBlock text:
                text.Foreground = foreground;
                break;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            ApplyDynamicColors(VisualTreeHelper.GetChild(element, index), background, foreground);
        }
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDiscardChanges())
        {
            return;
        }
        var name = TextPromptWindow.Ask(this, "Nuovo progetto", "Nome del progetto", "Nuovo progetto");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        _project = HmiProject.CreateStarter();
        _project.Name = name.Trim();
        _projectPath = null;
        _dirty = false;
        LoadProjectIntoEditor();
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDiscardChanges())
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Apri progetto HMI",
            Filter = "Progetto HMI (*.hmiproject)|*.hmiproject|File JSON (*.json)|*.json|Tutti i file (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            _project = await _storage.LoadAsync(dialog.FileName);
            _projectPath = dialog.FileName;
            _dirty = false;
            LoadProjectIntoEditor();
            StatusText.Text = "Progetto caricato";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Impossibile aprire il progetto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        ApplyWidgetInspector();
        if (string.IsNullOrWhiteSpace(_projectPath))
        {
            var dialog = new SaveFileDialog
            {
                Title = "Salva progetto HMI",
                Filter = "Progetto HMI (*.hmiproject)|*.hmiproject",
                DefaultExt = ".hmiproject",
                AddExtension = true,
                FileName = SanitizeFileName(_project.Name)
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            _projectPath = dialog.FileName;
        }
        try
        {
            await _storage.SaveAsync(_project, _projectPath);
            _dirty = false;
            UpdateProjectHeader();
            StatusText.Text = "Progetto salvato";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Impossibile salvare il progetto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportRuntime_Click(object sender, RoutedEventArgs e)
    {
        ApplyWidgetInspector();
        _project.Normalize();
        if (!ValidateRuntimeSecurityConfiguration())
        {
            return;
        }
        var startupPage = _project.Pages.FirstOrDefault(page => page.Id == _project.StartupPageId && page.Type == HmiPageType.Standard)
            ?? _project.Pages.FirstOrDefault(page => page.Type == HmiPageType.Standard);
        if (startupPage is null || !PageHasRuntimeExit(startupPage))
        {
            MessageBox.Show("Prima dell'esportazione inserire un pulsante 'Esci runtime' nella pagina iniziale o nel relativo template.",
                "Esporta runtime", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var localPanel = _project.Redundancy.Panels.FirstOrDefault(panel => panel.IsLocal);
        if (_project.Redundancy.Enabled && localPanel is null)
        {
            MessageBox.Show("Prima dell'esportazione selezionare il pannello locale nella scheda Ridondanza.",
                "Esporta runtime", MessageBoxButton.OK, MessageBoxImage.Information);
            InspectorTabs.SelectedIndex = InspectorTabs.Items.Count - 1;
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Esporta pacchetto solo runtime",
            Filter = "Pacchetto runtime HMI (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = SanitizeFileName(_project.Name) + "_Runtime"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            StatusText.Text = "Creazione pacchetto runtime…";
            await _runtimeExporter.ExportAsync(_project, dialog.FileName, localPanel?.Id);
            StatusText.Text = "Pacchetto runtime esportato";
            MessageBox.Show($"Pacchetto creato correttamente.\n\n{dialog.FileName}\n\nSul PC cliente estrarre lo ZIP e avviare HMI.exe.",
                "Esporta runtime", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Esportazione runtime non riuscita", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlcList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlcList.SelectedItem is not PlcConnectionDefinition plc)
        {
            return;
        }
        _editingPlc = plc;
        PopulatePlcForm(plc);
    }

    private void NewPlc_Click(object sender, RoutedEventArgs e)
    {
        _editingPlc = null;
        PlcList.SelectedItem = null;
        PopulatePlcForm(new PlcConnectionDefinition());
        PlcNameBox.Focus();
    }

    private void SavePlc_Click(object sender, RoutedEventArgs e)
    {
        var name = PlcNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Contains('.'))
        {
            MessageBox.Show("Il nome PLC è obbligatorio e non può contenere punti.", "Configurazione PLC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_project.PlcConnections.Any(plc => plc.Id != _editingPlc?.Id && plc.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Esiste già un PLC con questo nome.", "Configurazione PLC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var plcDefinition = _editingPlc ?? new PlcConnectionDefinition();
        plcDefinition.Name = name;
        plcDefinition.Driver = PlcDriverCombo.SelectedItem is PlcDriver driver ? driver : PlcDriver.SiemensS7;
        plcDefinition.Host = PlcHostBox.Text.Trim();
        plcDefinition.Port = ParseInt(PlcPortBox.Text, plcDefinition.Driver == PlcDriver.SiemensS7 ? 102 : 0);
        plcDefinition.CpuType = PlcCpuCombo.SelectedItem as string ?? "S71500";
        plcDefinition.Rack = (short)ParseInt(PlcRackBox.Text, 0);
        plcDefinition.Slot = (short)ParseInt(PlcSlotBox.Text, 1);
        if (_editingPlc is null)
        {
            _project.PlcConnections.Add(plcDefinition);
        }
        _editingPlc = plcDefinition;
        MarkDirty();
        RefreshCollections();
        PlcList.SelectedItem = plcDefinition;
    }

    private void DeletePlc_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPlc is null)
        {
            return;
        }
        var linkedTags = _project.Tags.Where(tag => tag.PlcId == _editingPlc.Id).ToList();
        var message = linkedTags.Count == 0
            ? $"Eliminare il PLC '{_editingPlc.Name}'?"
            : $"Eliminare il PLC '{_editingPlc.Name}' e le {linkedTags.Count} tag collegate?";
        if (MessageBox.Show(message, "Conferma eliminazione", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var tagIds = linkedTags.Select(tag => tag.Id).ToHashSet();
        _project.PlcConnections.Remove(_editingPlc);
        _project.Tags.RemoveAll(tag => tagIds.Contains(tag.Id));
        foreach (var widget in _project.Pages.SelectMany(page => page.Widgets).Where(widget => tagIds.Contains(widget.TagId)))
        {
            widget.TagId = string.Empty;
        }
        RemoveTagReferences(tagIds);
        _editingPlc = null;
        MarkDirty();
        RefreshCollections();
        RenderDesigner();
        PopulatePlcForm(new PlcConnectionDefinition());
    }

    private void PopulatePlcForm(PlcConnectionDefinition plc)
    {
        PlcNameBox.Text = plc.Name;
        PlcDriverCombo.SelectedItem = plc.Driver;
        PlcHostBox.Text = plc.Host;
        PlcPortBox.Text = plc.Port.ToString(CultureInfo.InvariantCulture);
        PlcCpuCombo.SelectedItem = plc.CpuType;
        PlcRackBox.Text = plc.Rack.ToString(CultureInfo.InvariantCulture);
        PlcSlotBox.Text = plc.Slot.ToString(CultureInfo.InvariantCulture);
        UpdatePlcFieldVisibility(plc.Driver);
    }

    private void PlcDriverCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlcDriverCombo.SelectedItem is PlcDriver driver)
        {
            UpdatePlcFieldVisibility(driver);
        }
    }

    private void UpdatePlcFieldVisibility(PlcDriver driver)
    {
        PlcNetworkFields.Visibility = driver == PlcDriver.Simulator ? Visibility.Collapsed : Visibility.Visible;
        PlcSiemensFields.Visibility = driver == PlcDriver.SiemensS7 ? Visibility.Visible : Visibility.Collapsed;
        PlcSimulatorInfo.Visibility = driver == PlcDriver.Simulator ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TagTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (TagTree.SelectedItem is not TreeViewItem item)
        {
            return;
        }
        if (item.Tag is TagDefinition tag)
        {
            _editingTag = tag;
            _selectedTagFolder = _project.TagFolders.FirstOrDefault(folder => folder.Id == tag.FolderId);
            PopulateTagForm(tag);
        }
        else if (item.Tag is TagFolderDefinition folder)
        {
            _selectedTagFolder = folder;
            _editingTag = null;
        }
    }

    private void TagTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TagTree.SelectedItem is TreeViewItem { Tag: TagFolderDefinition })
        {
            RenameSelectedTagFolder();
            e.Handled = true;
        }
    }

    private void NewTag_Click(object sender, RoutedEventArgs e)
    {
        _editingTag = null;
        PopulateTagForm(new TagDefinition
        {
            PlcId = _project.PlcConnections.FirstOrDefault()?.Id ?? string.Empty,
            FolderId = _selectedTagFolder?.Id ?? string.Empty
        });
        TagNameBox.Focus();
    }

    private void SaveTag_Click(object sender, RoutedEventArgs e)
    {
        var name = TagNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(TagAddressBox.Text) || TagPlcCombo.SelectedValue is not string plcId)
        {
            MessageBox.Show("Nome, PLC e indirizzo sono obbligatori.", "Definizione tag", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_project.Tags.Any(tag => tag.Id != _editingTag?.Id && tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Esiste già una tag con questo nome.", "Definizione tag", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var definition = _editingTag ?? new TagDefinition();
        definition.Name = name;
        definition.PlcId = plcId;
        definition.FolderId = TagFolderCombo.SelectedValue as string ?? string.Empty;
        definition.Address = TagAddressBox.Text.Trim();
        definition.DataType = TagDataTypeCombo.SelectedItem is TagDataType dataType ? dataType : TagDataType.Bool;
        definition.Access = TagAccessCombo.SelectedItem is TagAccess access ? access : TagAccess.ReadWrite;
        definition.PollIntervalMs = Math.Max(100, ParseInt(TagPollBox.Text, 500));
        definition.Description = TagDescriptionBox.Text.Trim();
        if (_editingTag is null)
        {
            _project.Tags.Add(definition);
        }
        _editingTag = definition;
        MarkDirty();
        RefreshCollections();
    }

    private void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (_editingTag is null || MessageBox.Show($"Eliminare la tag '{_editingTag.Name}'?", "Conferma eliminazione", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var id = _editingTag.Id;
        _project.Tags.Remove(_editingTag);
        foreach (var widget in _project.Pages.SelectMany(page => page.Widgets).Where(widget => widget.TagId == id))
        {
            widget.TagId = string.Empty;
        }
        RemoveTagReferences([id]);
        _editingTag = null;
        MarkDirty();
        RefreshCollections();
        RenderDesigner();
        PopulateTagForm(new TagDefinition());
    }

    private void RemoveTagReferences(IEnumerable<string> removedTagIds)
    {
        var tagIds = removedTagIds.ToHashSet();
        foreach (var book in _project.RecipeBooks)
        {
            book.TagIds.RemoveAll(tagIds.Contains);
            foreach (var recipe in book.Recipes)
            {
                foreach (var tagId in tagIds)
                {
                    recipe.Values.Remove(tagId);
                }
            }
        }
        _project.Alarms.RemoveAll(alarm => tagIds.Contains(alarm.TagId));
        _project.Database.TagLogging.RemoveAll(configuration => tagIds.Contains(configuration.TagId));
        foreach (var widget in _project.Pages.SelectMany(page => page.Widgets))
        {
            if (widget.Type == HmiWidgetType.TrendChart)
            {
                widget.ChartSeries.RemoveAll(series => tagIds.Contains(series.TagId));
                SyncLegacyChartTag(widget);
            }
            else if (tagIds.Contains(widget.TagId))
            {
                widget.TagId = string.Empty;
            }
            if (tagIds.Contains(widget.Animation.TagId))
            {
                widget.Animation.TagId = string.Empty;
            }
        }
    }

    private void PopulateTagForm(TagDefinition tag)
    {
        TagNameBox.Text = tag.Name;
        TagFolderCombo.SelectedValue = tag.FolderId;
        TagPlcCombo.SelectedValue = tag.PlcId;
        TagAddressBox.Text = tag.Address;
        TagDataTypeCombo.SelectedItem = tag.DataType;
        TagAccessCombo.SelectedItem = tag.Access;
        TagPollBox.Text = tag.PollIntervalMs.ToString(CultureInfo.InvariantCulture);
        TagDescriptionBox.Text = tag.Description;
    }

    private void RefreshTagTree(string? selectedTagId = null, string? selectedFolderId = null)
    {
        TagTree.Items.Clear();
        foreach (var folder in _project.TagFolders.Where(folder => string.IsNullOrWhiteSpace(folder.ParentFolderId)).OrderBy(folder => folder.Name))
        {
            TagTree.Items.Add(CreateFolderTreeItem(folder, selectedTagId, selectedFolderId));
        }
        foreach (var tag in _project.Tags.Where(tag => string.IsNullOrWhiteSpace(tag.FolderId)).OrderBy(tag => tag.Name))
        {
            TagTree.Items.Add(CreateTagTreeItem(tag, selectedTagId));
        }
    }

    private TreeViewItem CreateFolderTreeItem(TagFolderDefinition folder, string? selectedTagId, string? selectedFolderId)
    {
        var item = new TreeViewItem
        {
            Header = CreateTreeHeader("▰", folder.Name, "Cartella", "#F1B24A"),
            Tag = folder,
            IsExpanded = true,
            IsSelected = folder.Id == selectedFolderId,
            Foreground = BrushOf("#E8EEF5")
        };
        foreach (var child in _project.TagFolders.Where(candidate => candidate.ParentFolderId == folder.Id).OrderBy(candidate => candidate.Name))
        {
            item.Items.Add(CreateFolderTreeItem(child, selectedTagId, selectedFolderId));
        }
        foreach (var tag in _project.Tags.Where(candidate => candidate.FolderId == folder.Id).OrderBy(candidate => candidate.Name))
        {
            item.Items.Add(CreateTagTreeItem(tag, selectedTagId));
        }
        return item;
    }

    private TreeViewItem CreateTagTreeItem(TagDefinition tag, string? selectedTagId)
    {
        var item = new TreeViewItem
        {
            Header = CreateTreeHeader("◆", tag.Name, tag.Address, "#28C2B8"),
            Tag = tag,
            Foreground = BrushOf("#E8EEF5")
        };
        if (tag.Id == selectedTagId)
        {
            item.IsSelected = true;
        }
        return item;
    }

    private static FrameworkElement CreateTreeHeader(string icon, string title, string subtitle, string color)
    {
        var grid = new Grid { Margin = new Thickness(3, 4, 3, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = icon, Foreground = BrushOf(color), VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Foreground = BrushOf("#E8EEF5"), FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = subtitle, Foreground = BrushOf("#8FA0B3"), FontSize = 10 });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private List<FolderChoice> BuildFolderChoices()
    {
        var result = new List<FolderChoice> { new(string.Empty, "— Nessuna cartella —") };
        void AddChildren(string parentId, string prefix)
        {
            foreach (var folder in _project.TagFolders.Where(item => item.ParentFolderId == parentId).OrderBy(item => item.Name))
            {
                result.Add(new FolderChoice(folder.Id, prefix + folder.Name));
                AddChildren(folder.Id, prefix + folder.Name + " / ");
            }
        }
        AddChildren(string.Empty, string.Empty);
        return result;
    }

    private void NewTagFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Ask(this, "Nuova cartella tag", "Nome della cartella", "Nuova cartella");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var folder = new TagFolderDefinition { Name = name.Trim(), ParentFolderId = _selectedTagFolder?.Id ?? string.Empty };
        _project.TagFolders.Add(folder);
        _selectedTagFolder = folder;
        MarkDirty();
        RefreshCollections();
    }

    private void RenameTagFolder_Click(object sender, RoutedEventArgs e) => RenameSelectedTagFolder();

    private void RenameSelectedTagFolder()
    {
        if (TagTree.SelectedItem is not TreeViewItem { Tag: TagFolderDefinition selectedFolder })
        {
            MessageBox.Show("Selezionare prima una cartella nell'albero delle tag.", "Cartelle tag", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedTagFolder = selectedFolder;
        var name = TextPromptWindow.Ask(this, "Rinomina cartella tag", "Nuovo nome", _selectedTagFolder.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        name = name.Trim();
        if (_project.TagFolders.Any(folder => folder.Id != _selectedTagFolder.Id && folder.ParentFolderId == _selectedTagFolder.ParentFolderId && folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Esiste già una cartella con questo nome allo stesso livello.", "Cartelle tag", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedTagFolder.Name = name;
        _editingTag = null;
        MarkDirty();
        RefreshCollections();
        StatusText.Text = "Cartella tag rinominata";
    }

    private void DeleteTagFolder_Click(object sender, RoutedEventArgs e)
    {
        if (TagTree.SelectedItem is not TreeViewItem { Tag: TagFolderDefinition selectedFolder })
        {
            MessageBox.Show("Selezionare prima una cartella nell'albero delle tag.", "Cartelle tag", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedTagFolder = selectedFolder;
        if (MessageBox.Show($"Eliminare la cartella '{_selectedTagFolder.Name}'? Le tag verranno spostate nella cartella superiore.",
            "Cartelle tag", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var parentId = _selectedTagFolder.ParentFolderId;
        foreach (var tag in _project.Tags.Where(tag => tag.FolderId == _selectedTagFolder.Id))
        {
            tag.FolderId = parentId;
        }
        foreach (var folder in _project.TagFolders.Where(folder => folder.ParentFolderId == _selectedTagFolder.Id))
        {
            folder.ParentFolderId = parentId;
        }
        _project.TagFolders.Remove(_selectedTagFolder);
        _selectedTagFolder = _project.TagFolders.FirstOrDefault(folder => folder.Id == parentId);
        MarkDirty();
        RefreshCollections();
    }

    private void RecipeBookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecipeBookList.SelectedItem is not RecipeBookDefinition book)
        {
            return;
        }
        _editingRecipeBook = book;
        RecipeBookNameBox.Text = book.Name;
        RecipeTagList.SelectedItems.Clear();
        foreach (var tag in _project.Tags.Where(tag => book.TagIds.Contains(tag.Id)))
        {
            RecipeTagList.SelectedItems.Add(tag);
        }
    }

    private void NewRecipeBook_Click(object sender, RoutedEventArgs e)
    {
        _editingRecipeBook = null;
        RecipeBookList.SelectedItem = null;
        RecipeBookNameBox.Text = "Nuovo ricettario";
        RecipeTagList.SelectedItems.Clear();
        RecipeBookNameBox.Focus();
    }

    private void SaveRecipeBook_Click(object sender, RoutedEventArgs e)
    {
        var name = RecipeBookNameBox.Text.Trim();
        var selectedTags = RecipeTagList.SelectedItems.OfType<TagDefinition>().ToList();
        if (string.IsNullOrWhiteSpace(name) || selectedTags.Count == 0)
        {
            MessageBox.Show("Inserire un nome e selezionare almeno una tag.", "Ricettario", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var book = _editingRecipeBook ?? new RecipeBookDefinition();
        book.Name = name;
        book.TagIds = selectedTags.Select(tag => tag.Id).ToList();
        if (book.Recipes.Count == 0)
        {
            var recipe = new RecipeSetDefinition { Name = "Ricetta 1" };
            foreach (var tag in selectedTags)
            {
                recipe.Values[tag.Id] = tag.DataType == TagDataType.Bool ? "false" : "0";
            }
            book.Recipes.Add(recipe);
        }
        foreach (var recipe in book.Recipes)
        {
            foreach (var tagId in book.TagIds)
            {
                recipe.Values.TryAdd(tagId, "0");
            }
            foreach (var obsolete in recipe.Values.Keys.Where(id => !book.TagIds.Contains(id)).ToList())
            {
                recipe.Values.Remove(obsolete);
            }
        }
        if (_editingRecipeBook is null)
        {
            _project.RecipeBooks.Add(book);
        }
        _editingRecipeBook = book;
        MarkDirty();
        RefreshCollections();
        RecipeBookList.SelectedItem = book;
    }

    private void DeleteRecipeBook_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRecipeBook is null || MessageBox.Show($"Eliminare il ricettario '{_editingRecipeBook.Name}'?", "Ricettario",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var id = _editingRecipeBook.Id;
        _project.RecipeBooks.Remove(_editingRecipeBook);
        foreach (var widget in _project.Pages.SelectMany(page => page.Widgets).Where(widget => widget.RecipeBookId == id))
        {
            widget.RecipeBookId = string.Empty;
        }
        _editingRecipeBook = null;
        MarkDirty();
        RefreshCollections();
    }

    private void AlarmTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (AlarmTree.SelectedItem is not TreeViewItem item)
        {
            return;
        }
        if (item.Tag is AlarmFolderDefinition folder)
        {
            _selectedAlarmFolder = folder;
            _editingAlarm = null;
        }
        else if (item.Tag is AlarmDefinition alarm)
        {
            _editingAlarm = alarm;
            _selectedAlarmFolder = _project.AlarmFolders.FirstOrDefault(folder => folder.Id == alarm.FolderId);
            PopulateAlarmForm(alarm);
        }
    }

    private void AlarmTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AlarmTree.SelectedItem is TreeViewItem { Tag: AlarmFolderDefinition })
        {
            RenameSelectedAlarmFolder();
            e.Handled = true;
        }
    }

    private void NewAlarm_Click(object sender, RoutedEventArgs e)
    {
        _editingAlarm = null;
        PopulateAlarmForm(new AlarmDefinition
        {
            TagId = _project.Tags.FirstOrDefault()?.Id ?? string.Empty,
            FolderId = _selectedAlarmFolder?.Id ?? string.Empty
        });
        AlarmNameBox.Focus();
    }

    private void SaveAlarm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AlarmNameBox.Text) || AlarmTagCombo.SelectedValue is not string tagId)
        {
            MessageBox.Show("Nome e tag sorgente sono obbligatori.", "Allarme", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var alarm = _editingAlarm ?? new AlarmDefinition();
        alarm.Name = AlarmNameBox.Text.Trim();
        alarm.TagId = tagId;
        alarm.FolderId = AlarmFolderCombo.SelectedValue as string ?? string.Empty;
        alarm.Condition = AlarmConditionCombo.SelectedItem is AlarmCondition condition ? condition : AlarmCondition.True;
        alarm.TriggerValue = AlarmTriggerBox.Text.Trim();
        alarm.Severity = AlarmSeverityCombo.SelectedItem is AlarmSeverity severity ? severity : AlarmSeverity.Warning;
        alarm.Message = AlarmMessageBox.Text.Trim();
        alarm.RequiresAcknowledgement = AlarmAckCheck.IsChecked == true;
        if (_editingAlarm is null)
        {
            _project.Alarms.Add(alarm);
        }
        _editingAlarm = alarm;
        MarkDirty();
        RefreshCollections();
    }

    private void DeleteAlarm_Click(object sender, RoutedEventArgs e)
    {
        if (_editingAlarm is null || MessageBox.Show($"Eliminare l'allarme '{_editingAlarm.Name}'?", "Allarme",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        _project.Alarms.Remove(_editingAlarm);
        _activeAlarms.Remove(_editingAlarm.Id);
        _editingAlarm = null;
        MarkDirty();
        RefreshCollections();
    }

    private void PopulateAlarmForm(AlarmDefinition alarm)
    {
        AlarmNameBox.Text = alarm.Name;
        AlarmFolderCombo.SelectedValue = alarm.FolderId;
        AlarmTagCombo.SelectedValue = alarm.TagId;
        AlarmConditionCombo.SelectedItem = alarm.Condition;
        AlarmTriggerBox.Text = alarm.TriggerValue;
        AlarmSeverityCombo.SelectedItem = alarm.Severity;
        AlarmMessageBox.Text = alarm.Message;
        AlarmAckCheck.IsChecked = alarm.RequiresAcknowledgement;
    }

    private void RefreshAlarmTree(string? selectedAlarmId = null, string? selectedFolderId = null)
    {
        AlarmTree.Items.Clear();
        foreach (var folder in _project.AlarmFolders.Where(folder => string.IsNullOrWhiteSpace(folder.ParentFolderId)).OrderBy(folder => folder.Name))
        {
            AlarmTree.Items.Add(CreateAlarmFolderTreeItem(folder, selectedAlarmId, selectedFolderId));
        }
        foreach (var alarm in _project.Alarms.Where(alarm => string.IsNullOrWhiteSpace(alarm.FolderId)).OrderByDescending(alarm => alarm.Severity).ThenBy(alarm => alarm.Name))
        {
            AlarmTree.Items.Add(CreateAlarmTreeItem(alarm, selectedAlarmId));
        }
    }

    private TreeViewItem CreateAlarmFolderTreeItem(AlarmFolderDefinition folder, string? selectedAlarmId, string? selectedFolderId)
    {
        var item = new TreeViewItem
        {
            Header = CreateTreeHeader("▰", folder.Name, "Cartella allarmi", "#F1B24A"),
            Tag = folder,
            IsExpanded = true,
            IsSelected = folder.Id == selectedFolderId,
            Foreground = BrushOf("#E8EEF5")
        };
        foreach (var child in _project.AlarmFolders.Where(candidate => candidate.ParentFolderId == folder.Id).OrderBy(candidate => candidate.Name))
        {
            item.Items.Add(CreateAlarmFolderTreeItem(child, selectedAlarmId, selectedFolderId));
        }
        foreach (var alarm in _project.Alarms.Where(candidate => candidate.FolderId == folder.Id).OrderByDescending(candidate => candidate.Severity).ThenBy(candidate => candidate.Name))
        {
            item.Items.Add(CreateAlarmTreeItem(alarm, selectedAlarmId));
        }
        return item;
    }

    private TreeViewItem CreateAlarmTreeItem(AlarmDefinition alarm, string? selectedAlarmId)
    {
        var color = alarm.Severity switch
        {
            AlarmSeverity.Critical => "#EF5B5B",
            AlarmSeverity.Warning => "#F1B24A",
            _ => "#227CFF"
        };
        return new TreeViewItem
        {
            Header = CreateTreeHeader("!", alarm.Name, alarm.Severity.ToString(), color),
            Tag = alarm,
            IsSelected = alarm.Id == selectedAlarmId,
            Foreground = BrushOf("#E8EEF5")
        };
    }

    private List<FolderChoice> BuildAlarmFolderChoices()
    {
        var result = new List<FolderChoice> { new(string.Empty, "— Nessuna cartella —") };
        void Add(string parentId, string prefix)
        {
            foreach (var folder in _project.AlarmFolders.Where(folder => folder.ParentFolderId == parentId).OrderBy(folder => folder.Name))
            {
                result.Add(new FolderChoice(folder.Id, prefix + folder.Name));
                Add(folder.Id, prefix + folder.Name + " / ");
            }
        }
        Add(string.Empty, string.Empty);
        return result;
    }

    private void NewAlarmFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Ask(this, "Nuova cartella allarmi", "Nome della cartella", "Nuova cartella");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var folder = new AlarmFolderDefinition { Name = name.Trim(), ParentFolderId = _selectedAlarmFolder?.Id ?? string.Empty };
        _project.AlarmFolders.Add(folder);
        _selectedAlarmFolder = folder;
        MarkDirty();
        RefreshCollections();
    }

    private void RenameAlarmFolder_Click(object sender, RoutedEventArgs e) => RenameSelectedAlarmFolder();

    private void RenameSelectedAlarmFolder()
    {
        if (AlarmTree.SelectedItem is not TreeViewItem { Tag: AlarmFolderDefinition selectedFolder })
        {
            MessageBox.Show("Selezionare prima una cartella nell'albero degli allarmi.", "Cartelle allarmi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedAlarmFolder = selectedFolder;
        var name = TextPromptWindow.Ask(this, "Rinomina cartella allarmi", "Nuovo nome", selectedFolder.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        name = name.Trim();
        if (_project.AlarmFolders.Any(folder => folder.Id != selectedFolder.Id && folder.ParentFolderId == selectedFolder.ParentFolderId && folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Esiste già una cartella con questo nome allo stesso livello.", "Cartelle allarmi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        selectedFolder.Name = name;
        _editingAlarm = null;
        MarkDirty();
        RefreshCollections();
        StatusText.Text = "Cartella allarmi rinominata";
    }

    private void DeleteAlarmFolder_Click(object sender, RoutedEventArgs e)
    {
        if (AlarmTree.SelectedItem is not TreeViewItem { Tag: AlarmFolderDefinition selectedFolder })
        {
            MessageBox.Show("Selezionare prima una cartella nell'albero degli allarmi.", "Cartelle allarmi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedAlarmFolder = selectedFolder;
        if (MessageBox.Show($"Eliminare la cartella '{_selectedAlarmFolder.Name}'? Gli allarmi verranno spostati al livello superiore.",
            "Cartelle allarmi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var parentId = _selectedAlarmFolder.ParentFolderId;
        foreach (var alarm in _project.Alarms.Where(alarm => alarm.FolderId == _selectedAlarmFolder.Id))
        {
            alarm.FolderId = parentId;
        }
        foreach (var folder in _project.AlarmFolders.Where(folder => folder.ParentFolderId == _selectedAlarmFolder.Id))
        {
            folder.ParentFolderId = parentId;
        }
        _project.AlarmFolders.Remove(_selectedAlarmFolder);
        _selectedAlarmFolder = _project.AlarmFolders.FirstOrDefault(folder => folder.Id == parentId);
        MarkDirty();
        RefreshCollections();
    }

    private void PopulateSecuritySettingsForm()
    {
        SecurityEnabledCheck.IsChecked = _project.Security.Enabled;
        SecurityRequireLoginCheck.IsChecked = _project.Security.RequireLoginAtStartup;
        SecuritySessionTimeoutBox.Text = _project.Security.AutomaticLogoutMinutes.ToString(CultureInfo.InvariantCulture);
        SecurityAuditRetentionBox.Text = _project.Security.SessionHistoryRetentionDays.ToString(CultureInfo.InvariantCulture);
        SecurityMinimumPasswordLengthBox.Text = Math.Clamp(_project.Security.MinimumPasswordLength, 8, 128).ToString(CultureInfo.InvariantCulture);
        SecurityMaxFailedAttemptsBox.Text = _project.Security.MaximumFailedLoginAttempts.ToString(CultureInfo.InvariantCulture);
        SecurityLockoutMinutesBox.Text = _project.Security.LoginLockoutMinutes.ToString(CultureInfo.InvariantCulture);
    }

    private void SaveSecuritySettings_Click(object sender, RoutedEventArgs e)
    {
        _project.Security.Enabled = SecurityEnabledCheck.IsChecked == true;
        _project.Security.RequireLoginAtStartup = SecurityRequireLoginCheck.IsChecked == true;
        _project.Security.AutomaticLogoutMinutes = Math.Clamp(ParseInt(SecuritySessionTimeoutBox.Text, _project.Security.AutomaticLogoutMinutes), 0, 24 * 60);
        _project.Security.SessionHistoryRetentionDays = Math.Clamp(ParseInt(SecurityAuditRetentionBox.Text, _project.Security.SessionHistoryRetentionDays), 1, 3650);
        _project.Security.MinimumPasswordLength = Math.Clamp(ParseInt(SecurityMinimumPasswordLengthBox.Text, _project.Security.MinimumPasswordLength), 8, 128);
        _project.Security.MaximumFailedLoginAttempts = Math.Clamp(ParseInt(SecurityMaxFailedAttemptsBox.Text, _project.Security.MaximumFailedLoginAttempts), 1, 20);
        _project.Security.LoginLockoutMinutes = Math.Clamp(ParseInt(SecurityLockoutMinutesBox.Text, _project.Security.LoginLockoutMinutes), 1, 24 * 60);
        MarkDirty();
        PopulateSecuritySettingsForm();
        StatusText.Text = _project.Security.Enabled && !HasConfiguredAdministrator()
            ? "Sicurezza abilitata: creare almeno un amministratore attivo prima di avviare il runtime"
            : "Impostazioni sicurezza salvate";
    }

    private void UserList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UserList.SelectedItem is not UserDefinition user)
        {
            return;
        }
        _editingUser = user;
        PopulateUserForm(user);
    }

    private void NewUser_Click(object sender, RoutedEventArgs e)
    {
        _editingUser = null;
        PopulateUserForm(new UserDefinition
        {
            AccessLevel = _project.Security.Users.Count == 0 ? _project.Security.MaximumAccessLevel : Math.Min(10, _project.Security.MaximumAccessLevel),
            IsActive = true
        });
        UserList.SelectedItem = null;
        UserUsernameBox.Focus();
    }

    private void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        var password = UserPasswordBox.Password;
        if (!string.Equals(password, UserPasswordConfirmBox.Password, StringComparison.Ordinal))
        {
            MessageBox.Show("Le password inserite non coincidono.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_editingUser is null && string.IsNullOrEmpty(password))
        {
            MessageBox.Show("Per un nuovo utente è obbligatorio impostare una password.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!string.IsNullOrEmpty(password))
        {
            try
            {
                PasswordHashingService.ValidatePassword(password, _project.Security.MinimumPasswordLength);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var accessLevel = Math.Clamp(ParseInt(UserAccessLevelBox.Text, 0), 0, _project.Security.MaximumAccessLevel);
        var isActive = UserActiveCheck.IsChecked == true;
        if (_editingUser is not null && IsConfiguredAdministrator(_editingUser) &&
            (!isActive || accessLevel < _project.Security.MaximumAccessLevel) &&
            _project.Security.Users.Count(IsConfiguredAdministrator) <= 1)
        {
            MessageBox.Show("Non è possibile disattivare o declassare l'ultimo amministratore attivo.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var user = _editingUser is null
                ? _userSecurity.CreateUser(_project.Security, UserUsernameBox.Text, UserDisplayNameBox.Text, accessLevel, isActive, password)
                : _userSecurity.UpdateUser(_project.Security, _editingUser.Id, UserUsernameBox.Text, UserDisplayNameBox.Text, accessLevel, isActive);
            if (_editingUser is not null && !string.IsNullOrEmpty(password))
            {
                _userSecurity.ChangePassword(_project.Security, user.Id, password);
            }
            _editingUser = user;
            MarkDirty();
            RefreshCollections();
            UserList.SelectedItem = _project.Security.Users.First(item => item.Id == user.Id);
            StatusText.Text = $"Utente '{user.Username}' salvato";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (_editingUser is null)
        {
            return;
        }
        if (IsConfiguredAdministrator(_editingUser) && _project.Security.Users.Count(IsConfiguredAdministrator) <= 1)
        {
            MessageBox.Show("Non è possibile eliminare l'ultimo amministratore attivo.", "Gestione utenti", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"Eliminare l'utente '{_editingUser.Username}'?", "Gestione utenti", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        _userSecurity.DeleteUser(_project.Security, _editingUser.Id);
        _editingUser = null;
        MarkDirty();
        RefreshCollections();
        PopulateUserForm(new UserDefinition { AccessLevel = Math.Min(10, _project.Security.MaximumAccessLevel) });
        StatusText.Text = "Utente eliminato";
    }

    private void PopulateUserForm(UserDefinition user)
    {
        UserUsernameBox.Text = user.Username;
        UserDisplayNameBox.Text = user.DisplayName;
        UserAccessLevelBox.Text = user.AccessLevel.ToString(CultureInfo.InvariantCulture);
        UserActiveCheck.IsChecked = user.IsActive;
        UserPasswordBox.Clear();
        UserPasswordConfirmBox.Clear();
    }

    private bool IsConfiguredAdministrator(UserDefinition user) =>
        user.IsActive &&
        user.AccessLevel >= _project.Security.MaximumAccessLevel &&
        PasswordHashingService.HasValidCredential(user);

    private bool HasConfiguredAdministrator() => _project.Security.Users.Any(IsConfiguredAdministrator);

    private void PopulateDatabaseForm()
    {
        DatabaseEnabledCheck.IsChecked = _project.Database.Enabled;
        DatabaseHostBox.Text = _project.Database.Host;
        DatabasePortBox.Text = _project.Database.Port.ToString(CultureInfo.InvariantCulture);
        DatabaseUserBox.Text = _project.Database.Username;
        DatabasePasswordBox.Password = _project.Database.Password;
        var databases = new List<string> { _project.Database.DatabaseName };
        DatabaseNameCombo.ItemsSource = databases.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
        DatabaseNameCombo.SelectedItem = _project.Database.DatabaseName;
        DatabaseTableBox.Text = _project.Database.TableName;
        DatabaseRetentionDaysBox.Text = _project.Database.RetentionDays.ToString(CultureInfo.InvariantCulture);
    }

    private DatabaseSettings ReadDatabaseForm()
    {
        return new DatabaseSettings
        {
            Enabled = DatabaseEnabledCheck.IsChecked == true,
            Host = DatabaseHostBox.Text.Trim(),
            Port = Math.Clamp(ParseInt(DatabasePortBox.Text, 3306), 1, 65535),
            Username = DatabaseUserBox.Text.Trim(),
            Password = DatabasePasswordBox.Password,
            DatabaseName = DatabaseNameCombo.SelectedItem as string ?? DatabaseNameCombo.Text.Trim(),
            TableName = DatabaseTableBox.Text.Trim(),
            RetentionDays = Math.Clamp(ParseInt(DatabaseRetentionDaysBox.Text, 90), 1, 3650),
            TagLogging = _project.Database.TagLogging
        };
    }

    private async void TestDatabase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await new MySqlDatabaseService().TestConnectionAsync(ReadDatabaseForm());
            MessageBox.Show("Connessione MySQL riuscita.", "Database", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Connessione MySQL non riuscita", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshDatabases_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = DatabaseNameCombo.SelectedItem as string ?? _project.Database.DatabaseName;
            var databases = await new MySqlDatabaseService().GetDatabasesAsync(ReadDatabaseForm());
            DatabaseNameCombo.ItemsSource = databases;
            DatabaseNameCombo.SelectedItem = databases.Contains(current) ? current : databases.FirstOrDefault();
            StatusText.Text = $"Trovati {databases.Count} database MySQL";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Elenco database non disponibile", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CreateDatabase_Click(object sender, RoutedEventArgs e)
    {
        var databaseName = NewDatabaseNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            MessageBox.Show("Inserire il nome del database da creare.", "Database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var settings = ReadDatabaseForm();
            settings.DatabaseName = databaseName;
            var service = new MySqlDatabaseService();
            await service.CreateDatabaseAsync(settings);
            var databases = await service.GetDatabasesAsync(settings);
            DatabaseNameCombo.ItemsSource = databases;
            DatabaseNameCombo.SelectedItem = databases.FirstOrDefault(name => name.Equals(databaseName, StringComparison.OrdinalIgnoreCase));
            NewDatabaseNameBox.Clear();
            StatusText.Text = $"Database MySQL '{databaseName}' creato o già esistente";
            MessageBox.Show($"Database '{databaseName}' disponibile. Ora puoi selezionarlo e creare la tabella dello storico.",
                "Database", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Creazione database non riuscita", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CreateDatabaseTable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await new MySqlDatabaseService().CreateHistoryTableAsync(ReadDatabaseForm());
            MessageBox.Show("Tabella storico verificata o creata correttamente.", "Database", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Creazione tabella non riuscita", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteExpiredDatabaseRecords_Click(object sender, RoutedEventArgs e)
    {
        var settings = ReadDatabaseForm();
        if (string.IsNullOrWhiteSpace(settings.DatabaseName) || string.IsNullOrWhiteSpace(settings.TableName))
        {
            MessageBox.Show("Selezionare database e tabella.", "Retention database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Eliminare dalla tabella '{settings.TableName}' tutti i record più vecchi di {settings.RetentionDays} giorni?",
            "Retention database", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            var deleted = await new MySqlDatabaseService().DeleteOldRecordsAsync(settings, settings.DatabaseName, settings.TableName, settings.RetentionDays);
            MessageBox.Show($"Eliminati {deleted} record scaduti.", "Retention database", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Eliminazione record non riuscita", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveDatabaseSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = ReadDatabaseForm();
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.DatabaseName) || string.IsNullOrWhiteSpace(settings.TableName))
        {
            MessageBox.Show("Host, database e tabella sono obbligatori.", "Database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            MySqlDatabaseService.ValidateIdentifier(settings.DatabaseName, "database");
            MySqlDatabaseService.ValidateIdentifier(settings.TableName, "tabella");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _project.Database = settings;
        MarkDirty();
        StatusText.Text = "Configurazione MySQL salvata";
    }

    private void DatabaseTagList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DatabaseTagList.SelectedItem is not TagDefinition tag)
        {
            return;
        }
        _editingDatabaseTag = tag;
        var configuration = _project.Database.TagLogging.FirstOrDefault(item => item.TagId == tag.Id);
        DatabaseTagEnabledCheck.IsChecked = configuration?.Enabled == true;
        DatabaseLoggingModeCombo.SelectedItem = configuration?.Mode ?? DatabaseLoggingMode.OnChange;
        DatabaseLoggingIntervalBox.Text = (configuration?.IntervalMs ?? 1000).ToString(CultureInfo.InvariantCulture);
    }

    private void SaveDatabaseTagLogging_Click(object sender, RoutedEventArgs e)
    {
        if (_editingDatabaseTag is null)
        {
            return;
        }
        var configuration = _project.Database.TagLogging.FirstOrDefault(item => item.TagId == _editingDatabaseTag.Id);
        if (configuration is null)
        {
            configuration = new DatabaseTagLoggingDefinition { TagId = _editingDatabaseTag.Id };
            _project.Database.TagLogging.Add(configuration);
        }
        configuration.Enabled = DatabaseTagEnabledCheck.IsChecked == true;
        configuration.Mode = DatabaseLoggingModeCombo.SelectedItem is DatabaseLoggingMode mode ? mode : DatabaseLoggingMode.OnChange;
        configuration.IntervalMs = Math.Max(100, ParseInt(DatabaseLoggingIntervalBox.Text, 1000));
        MarkDirty();
        StatusText.Text = $"Storicizzazione tag '{_editingDatabaseTag.Name}' aggiornata";
    }

    private void SaveRedundancySettings_Click(object sender, RoutedEventArgs e)
    {
        _project.Redundancy.Enabled = RedundancyEnabledCheck.IsChecked == true;
        _project.Redundancy.FailoverDelayMs = Math.Max(0, ParseInt(RedundancyDelayBox.Text, 2000));
        _project.Redundancy.HealthCheckIntervalMs = Math.Max(1000, ParseInt(RedundancyHealthBox.Text, 5000));
        MarkDirty();
        StatusText.Text = "Impostazioni di ridondanza salvate";
    }

    private void RedundantPanelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RedundantPanelList.SelectedItem is not RedundantPanelDefinition panel)
        {
            return;
        }
        _editingRedundantPanel = panel;
        PopulateRedundantPanelForm(panel);
    }

    private void NewRedundantPanel_Click(object sender, RoutedEventArgs e)
    {
        _editingRedundantPanel = null;
        RedundantPanelList.SelectedItem = null;
        PopulateRedundantPanelForm(new RedundantPanelDefinition { Priority = _project.Redundancy.Panels.Count + 1 });
        RedundantPanelNameBox.Focus();
    }

    private void SaveRedundantPanel_Click(object sender, RoutedEventArgs e)
    {
        var name = RedundantPanelNameBox.Text.Trim();
        var host = RedundantPanelHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("Nome e indirizzo del pannello sono obbligatori.", "Ridondanza", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var panel = _editingRedundantPanel ?? new RedundantPanelDefinition();
        var priority = Math.Max(1, ParseInt(RedundantPanelPriorityBox.Text, 1));
        if (_project.Redundancy.Panels.Any(item => item.Id != panel.Id && item.Priority == priority))
        {
            MessageBox.Show("Ogni pannello deve avere una priorità diversa.", "Ridondanza", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        panel.Name = name;
        panel.Host = host;
        panel.Port = Math.Clamp(ParseInt(RedundantPanelPortBox.Text, 5000), 1, 65535);
        panel.Priority = priority;
        panel.IsLocal = RedundantPanelLocalCheck.IsChecked == true;
        if (panel.IsLocal)
        {
            foreach (var item in _project.Redundancy.Panels.Where(item => item.Id != panel.Id))
            {
                item.IsLocal = false;
            }
        }
        if (_editingRedundantPanel is null)
        {
            _project.Redundancy.Panels.Add(panel);
        }
        _editingRedundantPanel = panel;
        MarkDirty();
        RefreshCollections();
        RedundantPanelList.SelectedItem = panel;
    }

    private void DeleteRedundantPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRedundantPanel is null || MessageBox.Show($"Eliminare il pannello '{_editingRedundantPanel.Name}'?", "Ridondanza",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        _project.Redundancy.Panels.Remove(_editingRedundantPanel);
        _editingRedundantPanel = null;
        MarkDirty();
        RefreshCollections();
    }

    private void PopulateRedundantPanelForm(RedundantPanelDefinition panel)
    {
        RedundantPanelNameBox.Text = panel.Name;
        RedundantPanelHostBox.Text = panel.Host;
        RedundantPanelPortBox.Text = panel.Port.ToString(CultureInfo.InvariantCulture);
        RedundantPanelPriorityBox.Text = panel.Priority.ToString(CultureInfo.InvariantCulture);
        RedundantPanelLocalCheck.IsChecked = panel.IsLocal;
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateProjectHeader();
    }

    private void UpdateProjectHeader()
    {
        ProjectNameText.Text = _project.Name + (_dirty ? "  •" : string.Empty);
        ProjectPathText.Text = _projectPath is null ? "Non salvato" : System.IO.Path.GetFileName(_projectPath);
        Title = $"HMI Studio — {_project.Name}{(_dirty ? " *" : string.Empty)}";
    }

    private bool CanDiscardChanges()
    {
        if (!_dirty)
        {
            return true;
        }
        return MessageBox.Show("Le modifiche non salvate andranno perse. Continuare?", "HMI Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_runtimeMode && !_allowRuntimeClose)
        {
            e.Cancel = true;
            return;
        }
        if (_runtimeMode)
        {
            return;
        }
        ApplyWidgetInspector();
        if (!CanDiscardChanges())
        {
            e.Cancel = true;
            return;
        }
        _runtime.StopAsync().GetAwaiter().GetResult();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_runtimeMode)
        {
            RecordRuntimeUserActivity();
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key is Key.Left or Key.Right)
        {
            if (e.Key == Key.Left)
            {
                _leftSidebarCollapsed = !_leftSidebarCollapsed;
                ApplyLeftSidebarLayout();
            }
            else
            {
                _rightSidebarCollapsed = !_rightSidebarCollapsed;
                ApplyRightSidebarLayout();
            }
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Delete || Keyboard.FocusedElement is TextBoxBase or PasswordBox ||
            Keyboard.FocusedElement is DependencyObject focusedElement && IsDescendantOf(focusedElement, InspectorSidebar))
        {
            return;
        }
        DeleteWidget_Click(this, new RoutedEventArgs());
        e.Handled = true;
    }

    private static string WidgetTypeLabel(HmiWidgetType type) => type switch
    {
        HmiWidgetType.Label => "Testo",
        HmiWidgetType.Button => "Pulsante",
        HmiWidgetType.ValueDisplay => "Visualizzatore valore",
        HmiWidgetType.Indicator => "Spia di stato",
        HmiWidgetType.NumericInput => "Campo input",
        HmiWidgetType.Navigation => "Navigazione",
        HmiWidgetType.RecipeManager => "Gestione ricette",
        HmiWidgetType.AlarmViewer => "Visualizzatore allarmi",
        HmiWidgetType.AlarmHistoryViewer => "Storico allarmi",
        HmiWidgetType.DataHistoryViewer => "Storico dati",
        HmiWidgetType.TrendChart => "Grafico linea",
        HmiWidgetType.Image => "Immagine",
        HmiWidgetType.PopupButton => "Apri / chiudi popup",
        HmiWidgetType.PopupClose => "Chiudi popup",
        HmiWidgetType.RuntimeExit => "Esci runtime",
        HmiWidgetType.UserManager => "Gestione utenti",
        HmiWidgetType.LoginButton => "Login utente",
        HmiWidgetType.LogoutButton => "Logout utente",
        _ => "Oggetto"
    };

    private static bool SupportsTextAlignment(HmiWidgetType type) => type is
        HmiWidgetType.Label or
        HmiWidgetType.Button or
        HmiWidgetType.ValueDisplay or
        HmiWidgetType.NumericInput or
        HmiWidgetType.Navigation or
        HmiWidgetType.PopupButton or
        HmiWidgetType.PopupClose or
        HmiWidgetType.RuntimeExit or
        HmiWidgetType.LoginButton or
        HmiWidgetType.LogoutButton;

    private static bool SupportsDynamicAppearance(HmiWidgetType type) => type is
        HmiWidgetType.Button or
        HmiWidgetType.ValueDisplay or
        HmiWidgetType.NumericInput or
        HmiWidgetType.Indicator;

    private static HmiTextAlignment DefaultTextAlignment(HmiWidgetType type) => type is
        HmiWidgetType.Button or
        HmiWidgetType.Navigation or
        HmiWidgetType.PopupButton or
        HmiWidgetType.PopupClose or
        HmiWidgetType.RuntimeExit or
        HmiWidgetType.LoginButton or
        HmiWidgetType.LogoutButton
            ? HmiTextAlignment.Center
            : HmiTextAlignment.Left;

    private static TextAlignment ResolveTextAlignment(HmiWidgetDefinition widget)
    {
        var alignment = widget.TextAlignment == HmiTextAlignment.Default
            ? DefaultTextAlignment(widget.Type)
            : widget.TextAlignment;
        return alignment switch
        {
            HmiTextAlignment.Center => TextAlignment.Center,
            HmiTextAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };
    }

    private static HorizontalAlignment ToHorizontalAlignment(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => HorizontalAlignment.Center,
        TextAlignment.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Left
    };

    private static string UserSessionEndReasonLabel(UserSessionEndReason? reason) => reason switch
    {
        UserSessionEndReason.ManualLogout => "Logout manuale",
        UserSessionEndReason.AutomaticTimeout => "Timeout inattività",
        UserSessionEndReason.UserChanged => "Cambio utente",
        UserSessionEndReason.ReturnToDevelopment => "Ritorno allo sviluppo",
        UserSessionEndReason.ApplicationClosed => "Applicazione chiusa",
        UserSessionEndReason.RuntimeExit => "Uscita runtime",
        UserSessionEndReason.UserDeleted => "Utente eliminato",
        UserSessionEndReason.UnexpectedTermination => "Chiusura inattesa",
        _ => "—"
    };

    private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor))
            {
                return true;
            }
            DependencyObject? parent = null;
            try
            {
                parent = VisualTreeHelper.GetParent(element);
            }
            catch (InvalidOperationException)
            {
                // Alcuni elementi logici (per esempio gli item di una combo) non appartengono all'albero visuale.
            }
            element = parent ?? LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private static Brush BrushOf(string? value, string fallback = "#253244")
    {
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(value ?? fallback)!;
        }
        catch
        {
            try
            {
                return (Brush)new BrushConverter().ConvertFromString(fallback)!;
            }
            catch
            {
                return Brushes.Transparent;
            }
        }
    }

    private static bool IsValidBrushCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        try
        {
            return new BrushConverter().ConvertFromString(value.Trim()) is Brush;
        }
        catch
        {
            return false;
        }
    }

    private static double ParseDouble(string value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
            ? parsed
            : fallback;

    private static int ParseInt(string value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static DateTime? CombineLocalDateAndTime(DateTime? date, string? timeText, bool endOfDayWhenMissing)
    {
        if (date is null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(timeText) &&
            (TimeSpan.TryParse(timeText.Trim(), CultureInfo.CurrentCulture, out var time) ||
             TimeSpan.TryParse(timeText.Trim(), CultureInfo.InvariantCulture, out time)) &&
            time >= TimeSpan.Zero && time < TimeSpan.FromDays(1))
        {
            return date.Value.Date.Add(time);
        }
        return endOfDayWhenMissing ? date.Value.Date.AddDays(1) : date.Value.Date;
    }
    private static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(Math.Max(minimum, maximum), value));
    private static double Snap(double value) => Math.Round(value / SnapSize) * SnapSize;
    private static string FormatNumber(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);

    private static string FormatTagValue(object value, int decimals)
    {
        if (value is bool boolean)
        {
            return boolean ? "ON" : "OFF";
        }
        if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number.ToString($"F{Math.Clamp(decimals, 0, 6)}", CultureInfo.CurrentCulture);
        }
        return value?.ToString() ?? "—";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        return name;
    }

    private sealed class RuntimeWidgetBinding
    {
        public HmiWidgetDefinition? Widget { get; set; }
        public FrameworkElement? Root { get; set; }
        public TextBlock? ValueText { get; set; }
        public Ellipse? Indicator { get; set; }
        public TextBox? Input { get; set; }
        public RecipeBookDefinition? RecipeBook { get; set; }
        public ComboBox? RecipeCombo { get; set; }
        public StackPanel? RecipeValuesPanel { get; set; }
        public StackPanel? AlarmPanel { get; set; }
        public StackPanel? AlarmHistoryPanel { get; set; }
        public TextBox? AlarmSearchBox { get; set; }
        public ComboBox? AlarmSeverityCombo { get; set; }
        public ComboBox? AlarmStateCombo { get; set; }
        public DatePicker? AlarmFromPicker { get; set; }
        public DatePicker? AlarmToPicker { get; set; }
        public int AlarmHistoryRetentionDays { get; set; } = 90;
        public ComboBox? HistoryDatabaseCombo { get; set; }
        public ComboBox? HistoryTableCombo { get; set; }
        public TextBox? HistorySearchBox { get; set; }
        public DatePicker? HistoryFromPicker { get; set; }
        public TextBox? HistoryFromTimeBox { get; set; }
        public DatePicker? HistoryToPicker { get; set; }
        public TextBox? HistoryToTimeBox { get; set; }
        public TextBox? HistoryLimitBox { get; set; }
        public DataGrid? HistoryGrid { get; set; }
        public TextBlock? HistoryStatus { get; set; }
        public int HistoryDatabaseRequestVersion { get; set; }
        public int HistoryTableRequestVersion { get; set; }
        public int HistoryQueryRequestVersion { get; set; }
        public Canvas? ChartCanvas { get; set; }
        public TextBlock? ChartStatus { get; set; }
        public List<RuntimeChartSeriesBinding>? ChartSeriesBindings { get; set; }
        public List<Line>? ChartGridLines { get; set; }
        public TextBlock? ChartMaxLabel { get; set; }
        public TextBlock? ChartMinLabel { get; set; }
        public TextBlock? ChartStartTimeLabel { get; set; }
        public TextBlock? ChartEndTimeLabel { get; set; }
        public DateTime LastChartRenderUtc { get; set; } = DateTime.MinValue;
        public bool ChartRenderScheduled { get; set; }
        public int ChartHistoricalRequestVersion { get; set; }
        public ListBox? UserList { get; set; }
        public TextBox? UserUsernameBox { get; set; }
        public TextBox? UserDisplayNameBox { get; set; }
        public TextBox? UserAccessLevelBox { get; set; }
        public CheckBox? UserActiveCheck { get; set; }
        public PasswordBox? UserPasswordBox { get; set; }
        public PasswordBox? UserPasswordConfirmBox { get; set; }
        public DataGrid? UserAuditGrid { get; set; }
        public TextBlock? UserSessionStatus { get; set; }
        public FrameworkElement? UserProtectedContent { get; set; }
        public FrameworkElement? UserAccessDeniedContent { get; set; }
    }

    private sealed class RuntimeChartSeriesBinding
    {
        public required ChartSeriesDefinition Definition { get; init; }
        public required Polyline Line { get; init; }
        public List<HistoryDataPoint> Points { get; set; } = [];
    }

    private sealed record HistoricalTrendSeriesResult(RuntimeChartSeriesBinding Series, List<HistoryDataPoint> Points, string? Error);

    private sealed record UserSessionRuntimeRow(string User, int AccessLevel, string Login, string Logout, string Reason);

    private sealed class AlarmRuntimeState
    {
        public required string OccurrenceId { get; init; }
        public required DateTime ActivatedAtUtc { get; init; }
        public DateTime? ResolvedAtUtc { get; set; }
        public bool IsAcknowledged { get; set; }
    }

    private sealed record FolderChoice(string Id, string DisplayPath)
    {
        public override string ToString() => DisplayPath;
    }

    private sealed record ChartSeriesEditorItem(ChartSeriesDefinition Series, string DisplayName, string TagName, string Color)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record AnimationConditionOption(HmiDynamicCondition Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record AnimationRuleEditorItem(
        HmiAnimationRuleDefinition Rule,
        int Priority,
        string Name,
        string Summary,
        string Background)
    {
        public override string ToString() => Name;
    }
}

internal sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;

    private TextPromptWindow(Window owner, string title, string label, string initialValue)
    {
        Owner = owner;
        Title = title;
        Width = 420;
        Height = 205;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 26, 36));

        _input = new TextBox
        {
            Text = initialValue,
            Margin = new Thickness(0, 7, 0, 18),
            Padding = new Thickness(9, 7, 9, 7),
            Background = new SolidColorBrush(Color.FromRgb(13, 21, 30)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(38, 53, 69))
        };
        _input.SelectAll();

        var confirm = new Button
        {
            Content = "Conferma",
            IsDefault = true,
            Width = 100,
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(40, 194, 184)),
            Foreground = new SolidColorBrush(Color.FromRgb(6, 21, 20)),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        confirm.Click += (_, _) => DialogResult = true;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(143, 160, 179)) });
        panel.Children.Add(_input);
        panel.Children.Add(confirm);
        Content = panel;
        Loaded += (_, _) => _input.Focus();
    }

    public static string? Ask(Window owner, string title, string label, string initialValue)
    {
        var dialog = new TextPromptWindow(owner, title, label, initialValue);
        return dialog.ShowDialog() == true ? dialog._input.Text : null;
    }
}

internal sealed class LoginWindow : Window
{
    private readonly TextBox _username;
    private readonly PasswordBox _password;

    private LoginWindow(Window owner, string title, string initialUsername)
    {
        Owner = owner;
        Title = title;
        Width = 440;
        Height = 310;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 26, 36));
        WindowStyle = WindowStyle.SingleBorderWindow;
        _username = new TextBox
        {
            Text = initialUsername,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.FromRgb(13, 21, 30)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(38, 53, 69))
        };
        _password = new PasswordBox
        {
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.FromRgb(13, 21, 30)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(38, 53, 69))
        };
        var confirm = new Button
        {
            Content = "ACCEDI",
            IsDefault = true,
            Width = 105,
            Height = 36,
            Background = new SolidColorBrush(Color.FromRgb(40, 194, 184)),
            Foreground = new SolidColorBrush(Color.FromRgb(6, 21, 20)),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold
        };
        confirm.Click += (_, _) => DialogResult = true;
        var cancel = new Button
        {
            Content = "ANNULLA",
            IsCancel = true,
            Width = 105,
            Height = 36,
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(38, 54, 70)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "AUTENTICAZIONE OPERATORE", Foreground = new SolidColorBrush(Color.FromRgb(40, 194, 184)), FontWeight = FontWeights.Bold, FontSize = 15, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Nome utente", Foreground = new SolidColorBrush(Color.FromRgb(143, 160, 179)), Margin = new Thickness(0, 4, 0, 4) });
        panel.Children.Add(_username);
        panel.Children.Add(new TextBlock { Text = "Password", Foreground = new SolidColorBrush(Color.FromRgb(143, 160, 179)), Margin = new Thickness(0, 10, 0, 4) });
        panel.Children.Add(_password);
        panel.Children.Add(actions);
        Content = panel;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_username.Text))
            {
                _username.Focus();
            }
            else
            {
                _username.SelectAll();
                _password.Focus();
            }
        };
    }

    public static LoginCredentials? Ask(Window owner, string title, string initialUsername)
    {
        var dialog = new LoginWindow(owner, title, initialUsername);
        return dialog.ShowDialog() == true
            ? new LoginCredentials(dialog._username.Text.Trim(), dialog._password.Password)
            : null;
    }
}

internal sealed record LoginCredentials(string Username, string Password);
