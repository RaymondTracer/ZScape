using System.ComponentModel;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.UI;

/// <summary>
/// Custom control for displaying player list with proper dark theme support.
/// Avoids ListView quirks with column sizing and scrollbars.
/// </summary>
public class PlayerListControl : Control
{
    private readonly List<PlayerDisplayInfo> _players = new();
    private readonly VScrollBar _scrollBar;
    private int _scrollOffset;
    private int _hoveredRow = -1;
    private int _selectedRow = -1;
    
    // Column definitions
    private readonly ColumnDef[] _columns;
    
    // Layout constants
    private const int RowHeight = 20;
    private const int HeaderHeight = 22;
    private const int MinNameWidth = 80;
    
    // Colors (dark theme)
    private readonly Color _headerBg = Color.FromArgb(45, 45, 48);
    private readonly Color _headerBorder = Color.FromArgb(62, 62, 66);
    private readonly Color _rowBgEven = Color.FromArgb(30, 30, 30);
    private readonly Color _rowBgOdd = Color.FromArgb(35, 35, 35);
    private readonly Color _rowBgHover = Color.FromArgb(50, 50, 55);
    private readonly Color _rowBgSelected = Color.FromArgb(0, 122, 204);
    private readonly Color _textPrimary = Color.FromArgb(241, 241, 241);
    private readonly Color _textSecondary = Color.FromArgb(153, 153, 153);
    private readonly Color _textDisabled = Color.FromArgb(100, 100, 100);
    
    private class ColumnDef
    {
        public string Header { get; set; } = "";
        public int Width { get; set; }
        public int MinWidth { get; set; }
        public bool FillRemaining { get; set; }
        public HorizontalAlignment Alignment { get; set; } = HorizontalAlignment.Left;
    }
    
    private class PlayerDisplayInfo
    {
        public string Name { get; set; } = "";
        public string Score { get; set; } = "";
        public string Ping { get; set; } = "";
        public string Team { get; set; } = "";
        public bool IsSpectator { get; set; }
        public bool IsBot { get; set; }
    }
    
    public PlayerListControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | 
                 ControlStyles.UserPaint | 
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        
        BackColor = _rowBgEven;
        
        _columns = new ColumnDef[]
        {
            new() { Header = "Name", MinWidth = MinNameWidth, FillRemaining = true },
            new() { Header = "Score", Width = 50, MinWidth = 50, Alignment = HorizontalAlignment.Right },
            new() { Header = "Ping", Width = 50, MinWidth = 50, Alignment = HorizontalAlignment.Right },
            new() { Header = "Team", Width = 50, MinWidth = 50 }
        };
        
        _scrollBar = new VScrollBar
        {
            Dock = DockStyle.Right,
            Visible = false,
            SmallChange = 1,
            LargeChange = 5
        };
        _scrollBar.Scroll += (s, e) =>
        {
            _scrollOffset = _scrollBar.Value;
            Invalidate();
        };
        Controls.Add(_scrollBar);
    }
    
    public void SetPlayers(IEnumerable<PlayerInfo> players, TeamInfo[] teams)
    {
        _players.Clear();
        
        foreach (var player in players)
        {
            string teamName = player.Team >= 0 && player.Team < teams.Length 
                ? teams[player.Team].Name 
                : "-";
            
            string name = DoomColorCodes.StripColorCodes(player.Name);
            if (player.IsBot) name += " [BOT]";
            
            _players.Add(new PlayerDisplayInfo
            {
                Name = name,
                Score = player.Score.ToString(),
                Ping = player.Ping.ToString(),
                Team = teamName,
                IsSpectator = player.IsSpectator,
                IsBot = player.IsBot
            });
        }
        
        UpdateScrollBar();
        CalculateColumnWidths();
        Invalidate();
    }
    
    public void Clear()
    {
        _players.Clear();
        _scrollOffset = 0;
        _selectedRow = -1;
        _hoveredRow = -1;
        UpdateScrollBar();
        Invalidate();
    }
    
    public int PlayerCount => _players.Count;
    
    private void UpdateScrollBar()
    {
        int contentHeight = _players.Count * RowHeight;
        int visibleHeight = ClientSize.Height - HeaderHeight;
        
        if (contentHeight > visibleHeight && visibleHeight > 0)
        {
            _scrollBar.Visible = true;
            _scrollBar.Maximum = _players.Count - 1;
            _scrollBar.LargeChange = Math.Max(1, visibleHeight / RowHeight);
            if (_scrollOffset > _scrollBar.Maximum - _scrollBar.LargeChange + 1)
            {
                _scrollOffset = Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1);
            }
            _scrollBar.Value = _scrollOffset;
        }
        else
        {
            _scrollBar.Visible = false;
            _scrollOffset = 0;
        }
    }
    
    private void CalculateColumnWidths()
    {
        int availableWidth = ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0);
        
        // Calculate team column width based on content
        int maxTeamWidth = _columns[3].MinWidth;
        if (_players.Count > 0)
        {
            using var g = CreateGraphics();
            foreach (var player in _players)
            {
                var size = TextRenderer.MeasureText(g, player.Team, Font);
                maxTeamWidth = Math.Max(maxTeamWidth, size.Width + 10);
            }
            maxTeamWidth = Math.Min(maxTeamWidth, 120); // Cap at 120
        }
        _columns[3].Width = maxTeamWidth;
        
        // Fixed columns take their width
        int fixedWidth = _columns[1].Width + _columns[2].Width + _columns[3].Width;
        
        // Name column fills the rest
        _columns[0].Width = Math.Max(_columns[0].MinWidth, availableWidth - fixedWidth);
    }
    
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollBar();
        CalculateColumnWidths();
    }
    
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_scrollBar.Visible)
        {
            int delta = e.Delta > 0 ? -3 : 3;
            int newValue = Math.Clamp(_scrollOffset + delta, 0, 
                Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1));
            if (newValue != _scrollOffset)
            {
                _scrollOffset = newValue;
                _scrollBar.Value = _scrollOffset;
                Invalidate();
            }
        }
    }
    
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int row = GetRowAtPoint(e.Location);
        if (row != _hoveredRow)
        {
            _hoveredRow = row;
            Invalidate();
        }
    }
    
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredRow != -1)
        {
            _hoveredRow = -1;
            Invalidate();
        }
    }
    
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int row = GetRowAtPoint(e.Location);
        if (row >= 0 && row < _players.Count)
        {
            _selectedRow = row;
            Invalidate();
        }
    }
    
    private int GetRowAtPoint(Point pt)
    {
        if (pt.Y < HeaderHeight) return -1;
        int row = (pt.Y - HeaderHeight) / RowHeight + _scrollOffset;
        return row >= 0 && row < _players.Count ? row : -1;
    }
    
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        
        int contentWidth = ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0);
        
        // Draw header
        DrawHeader(g, contentWidth);
        
        // Draw rows
        int y = HeaderHeight;
        int visibleRows = (ClientSize.Height - HeaderHeight) / RowHeight + 1;
        
        for (int i = 0; i < visibleRows && (_scrollOffset + i) < _players.Count; i++)
        {
            int playerIndex = _scrollOffset + i;
            DrawRow(g, playerIndex, y, contentWidth);
            y += RowHeight;
        }
        
        // Fill remaining space if any
        if (y < ClientSize.Height)
        {
            using var brush = new SolidBrush(_rowBgEven);
            g.FillRectangle(brush, 0, y, contentWidth, ClientSize.Height - y);
        }
    }
    
    private void DrawHeader(Graphics g, int contentWidth)
    {
        // Header background
        using (var brush = new SolidBrush(_headerBg))
        {
            g.FillRectangle(brush, 0, 0, contentWidth, HeaderHeight);
        }
        
        // Header border
        using (var pen = new Pen(_headerBorder))
        {
            g.DrawLine(pen, 0, HeaderHeight - 1, contentWidth, HeaderHeight - 1);
        }
        
        // Column headers
        int x = 0;
        using var textBrush = new SolidBrush(_textPrimary);
        
        foreach (var col in _columns)
        {
            // Column separator
            if (x > 0)
            {
                using var pen = new Pen(_headerBorder);
                g.DrawLine(pen, x, 0, x, HeaderHeight);
            }
            
            var format = new StringFormat
            {
                Alignment = col.Alignment switch
                {
                    HorizontalAlignment.Right => StringAlignment.Far,
                    HorizontalAlignment.Center => StringAlignment.Center,
                    _ => StringAlignment.Near
                },
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            
            var textRect = new RectangleF(x + 4, 0, col.Width - 8, HeaderHeight);
            g.DrawString(col.Header, Font, textBrush, textRect, format);
            
            x += col.Width;
        }
        
        // Fill remaining header area to avoid white gap
        if (x < contentWidth)
        {
            using var brush = new SolidBrush(_headerBg);
            g.FillRectangle(brush, x, 0, contentWidth - x, HeaderHeight);
        }
    }
    
    private void DrawRow(Graphics g, int playerIndex, int y, int contentWidth)
    {
        var player = _players[playerIndex];
        
        // Row background
        Color bgColor;
        if (playerIndex == _selectedRow)
            bgColor = _rowBgSelected;
        else if (playerIndex == _hoveredRow)
            bgColor = _rowBgHover;
        else
            bgColor = playerIndex % 2 == 0 ? _rowBgEven : _rowBgOdd;
        
        using (var brush = new SolidBrush(bgColor))
        {
            g.FillRectangle(brush, 0, y, contentWidth, RowHeight);
        }
        
        // Text color
        Color textColor;
        if (playerIndex == _selectedRow)
            textColor = Color.White;
        else if (player.IsBot)
            textColor = _textDisabled;
        else if (player.IsSpectator)
            textColor = _textSecondary;
        else
            textColor = _textPrimary;
        
        using var textBrush = new SolidBrush(textColor);
        
        // Draw cells
        int x = 0;
        string[] values = { player.Name, player.Score, player.Ping, player.Team };
        
        for (int i = 0; i < _columns.Length; i++)
        {
            var col = _columns[i];
            
            var format = new StringFormat
            {
                Alignment = col.Alignment switch
                {
                    HorizontalAlignment.Right => StringAlignment.Far,
                    HorizontalAlignment.Center => StringAlignment.Center,
                    _ => StringAlignment.Near
                },
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            
            var textRect = new RectangleF(x + 4, y, col.Width - 8, RowHeight);
            g.DrawString(values[i], Font, textBrush, textRect, format);
            
            x += col.Width;
        }
        
        // Fill remaining row area
        if (x < contentWidth)
        {
            using var brush = new SolidBrush(bgColor);
            g.FillRectangle(brush, x, y, contentWidth - x, RowHeight);
        }
    }
}
