using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ThorCapture
{
    // Folding platform groups in the game grid.
    internal static class GameGridTests
    {
        internal static void Run()
        {
            string saved = Settings.Get("library.collapsed", "");
            Settings.Set("library.collapsed", "");
            try
            {
                List<GameEntry> games = new List<GameEntry>();
                int id = 1;
                foreach (string platform in new[] { "nds", "gba", "switch" })
                    for (int i = 0; i < 7; i++) games.Add(new GameEntry { Id = id++, Name = platform + " " + i, Platform = platform, FileName = platform + i, HasCover = i % 2 == 0 });
                using (GameGridView grid = new GameGridView { Size = new Size(900, 700) })
                {
                    grid.SetGames(games, -1);
                    Tests.Check(grid.HeaderCount == 3 && games.TrueForAll(game => !game.Bounds.IsEmpty), "each platform gets a header and every card is laid out");
                    float gbaTop = games[7].Bounds.Y;
                    grid.Toggle(0);
                    Tests.Check(grid.IsFolded("nds") && games.GetRange(0, 7).TrueForAll(game => game.Bounds.IsEmpty), "folding a group hides its cards");
                    Tests.Check(games[7].Bounds.Y < gbaTop, "the groups below move up into the freed space");
                    Tests.Check(Settings.Get("library.collapsed", "") == "nds", "the folded group is remembered");
                    grid.Select(7);
                    grid.Select(-1);
                    using (GameGridView again = new GameGridView { Size = new Size(900, 700) })
                    {
                        List<GameEntry> copy = new List<GameEntry>();
                        foreach (GameEntry game in games) copy.Add(new GameEntry { Id = game.Id, Name = game.Name, Platform = game.Platform, FileName = game.FileName });
                        again.SetGames(copy, -1);
                        Tests.Check(again.IsFolded("nds") && copy[0].Bounds.IsEmpty && !copy[7].Bounds.IsEmpty, "a new view starts with the remembered groups folded");
                    }
                    grid.ToggleAll();
                    Tests.Check(grid.IsFolded("gba") && grid.IsFolded("switch") && games.TrueForAll(game => game.Bounds.IsEmpty), "folding all hides every card");
                    grid.ToggleAll();
                    Tests.Check(!grid.IsFolded("nds") && games.TrueForAll(game => !game.Bounds.IsEmpty) && Settings.Get("library.collapsed", "") == "", "unfolding all brings every card back");
                    grid.Toggle(1); // fold gba
                    grid.Select(6); // last nds game
                    typeof(GameGridView).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(grid, new object[] { new KeyEventArgs(Keys.Right) });
                    Tests.Check(grid.Selected != null && grid.Selected.Platform == "switch", "the arrow keys step over a folded group [" + (grid.Selected == null ? "-" : grid.Selected.Name) + "]");
                }
            }
            finally { Settings.Set("library.collapsed", saved); }
        }
    }
}
