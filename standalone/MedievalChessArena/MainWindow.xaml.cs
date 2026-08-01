using MedievalChessArena.Chess;
using MedievalChessArena.Connections;
using MedievalChessArena.Rendering;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MedievalChessArena;

public partial class MainWindow : Window
{
    private readonly ArenaSession _session = new();
    private readonly BoardScene _scene = new();
    private readonly ArenaServerHost _server;
    private Square? _selected;
    private bool _flipped;
    private bool _ready;
    private bool _refreshingControllers;

    public MainWindow()
    {
        InitializeComponent();
        _server = new ArenaServerHost(_session);
        SceneVisual.Content = _scene.Root;
        ApiEndpointText.Text = _server.ApiEndpoint;
        CodexMcpEndpointText.Text = _server.CodexMcpEndpoint;
        AliMcpEndpointText.Text = _server.AliMcpEndpoint;
        _session.Changed += SessionChanged;
        Loaded += WindowLoaded;
        Closed += WindowClosed;
        _ready = true;
        Refresh();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _server.StartAsync();
            CodexGateState.Text = "Open • five Codex-bound tools ready";
            AliGateState.Text = "Open • five Ali-bound tools ready";
            CodexGateState.Foreground = new SolidColorBrush(Color.FromRgb(109, 223, 153));
            AliGateState.Foreground = new SolidColorBrush(Color.FromRgb(109, 223, 153));
        }
        catch (Exception ex)
        {
            CodexGateState.Text = $"Gate unavailable: {ex.Message}";
            AliGateState.Text = "Gate unavailable";
            CodexGateState.Foreground = new SolidColorBrush(Color.FromRgb(255, 122, 104));
            AliGateState.Foreground = new SolidColorBrush(Color.FromRgb(255, 122, 104));
        }
    }

    private async void WindowClosed(object? sender, EventArgs e) => await _server.DisposeAsync();

    private void SessionChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var state = _session.GetSnapshot();
        _scene.Rebuild(state, _selected);
        TurnText.Text = $"{state.SideToMove} to move";
        StatusText.Text = state.Status + (state.Winner is null ? string.Empty : $" • {state.Winner} victorious");
        FooterText.Text = $"White: {state.WhiteController}   •   Black: {state.BlackController}   •   Position {state.Version}";
        MoveHistory.ItemsSource = state.MoveHistory;
        if (MoveHistory.Items.Count > 0) MoveHistory.ScrollIntoView(MoveHistory.Items[^1]);
        _refreshingControllers = true;
        try
        {
            SetControllerSelection(WhiteController, state.WhiteController);
            SetControllerSelection(BlackController, state.BlackController);
        }
        finally
        {
            _refreshingControllers = false;
        }
    }

    private void BoardClicked(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(ArenaViewport);
        VisualTreeHelper.HitTest(ArenaViewport, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult hit && _scene.TryGetSquare(hit.ModelHit, out var square))
            {
                HandleSquare(square);
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
    }

    private void HandleSquare(Square square)
    {
        var state = _session.GetSnapshot();
        if (_selected is null)
        {
            var ownPiece = state.Pieces.Any(p => p.Square == square.Algebraic && p.Color == state.SideToMove);
            if (!ownPiece) { MoveMessage.Text = $"No {state.SideToMove} champion stands on {square}."; return; }
            _selected = square;
            SelectionText.Text = $"{square} selected • choose its destination";
            Refresh();
            return;
        }

        var from = _selected.Value;
        _selected = null;
        SelectionText.Text = "Choose your champion.";
        MoveBox.Text = from.Algebraic + square.Algebraic;
        MakeHumanMove(MoveBox.Text);
    }

    private void SubmitMove(object sender, RoutedEventArgs e) => MakeHumanMove(MoveBox.Text);

    private void MoveBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) MakeHumanMove(MoveBox.Text);
    }

    private void MakeHumanMove(string move)
    {
        var result = _session.Move("Human", move);
        MoveMessage.Text = result.Message;
        if (!result.Success) Refresh();
    }

    private void ControllerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshingControllers) return;
        if (sender == WhiteController) _session.Claim(SelectedController(WhiteController), "White");
        else if (sender == BlackController) _session.Claim(SelectedController(BlackController), "Black");
    }

    private void ResetGame(object sender, RoutedEventArgs e)
    {
        _selected = null;
        _session.Reset();
        MoveMessage.Text = "A new battle has begun.";
    }

    private void FlipBoard(object sender, RoutedEventArgs e)
    {
        _flipped = !_flipped;
        ArenaCamera.Position = _flipped ? new Point3D(0, 9.8, 10.7) : new Point3D(0, 9.8, -10.7);
        ArenaCamera.LookDirection = _flipped ? new Vector3D(0, -8.4, -10.2) : new Vector3D(0, -8.4, 10.2);
    }

    private void ExportPgn(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export the battle chronicle", Filter = "Portable Game Notation (*.pgn)|*.pgn", FileName = "Medieval-Chess-Arena.pgn" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, _session.ExportPgn());
        MoveMessage.Text = $"Chronicle exported to {dialog.FileName}.";
    }

    private static string SelectedController(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Human";

    private static void SetControllerSelection(ComboBox box, string controller)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Content?.ToString(), controller, StringComparison.OrdinalIgnoreCase))
            { box.SelectedItem = item; return; }
    }
}
