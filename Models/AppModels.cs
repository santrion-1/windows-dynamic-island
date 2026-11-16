using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DynamicIsland.Models
{
    public class TodoItemModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public bool IsCompleted { get; set; }
    }

    public class ShelfItemModel
    {
        public string FilePath { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string SizeText { get; set; } = "";
        public bool IsFolder { get; set; }
        public bool IsImage { get; set; }
    }

    public class WaterStateModel
    {
        public string DateKey { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
        public double CurrentLiters { get; set; } = 0.0;
        public double GoalLiters { get; set; } = 1.5;
        public double CupSizeLiters { get; set; } = 0.25;
        public bool RemindersEnabled { get; set; } = false;
        public int ReminderIntervalMinutes { get; set; } = 60;
        public string NextReminderText { get; set; } = "--:--";
        public int SubViewIndex { get; set; } = 0; // 0 = Ring Gauge, 1 = Hourly Bar Chart
        public Dictionary<int, int> HourlyIntakeMl { get; set; } = new();
    }

    public class AppPersistentState
    {
        public WaterStateModel Water { get; set; } = new();
        public List<TodoItemModel> Todos { get; set; } = new();
        public List<ShelfItemModel> ShelfItems { get; set; } = new();
        public bool IsPinned { get; set; } = false;
        public int LastPageIndex { get; set; } = 0;
        public double? CustomLeft { get; set; } = null;
    }

    public static class AppStorage
    {
        private static readonly string StorageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynamicIslandApp");

        private static readonly string StorageFile = Path.Combine(StorageDir, "state_clean.json");

        public static AppPersistentState Load()
        {
            try
            {
                if (File.Exists(StorageFile))
                {
                    string json = File.ReadAllText(StorageFile);
                    var state = JsonSerializer.Deserialize<AppPersistentState>(json);
                    if (state != null)
                    {
                        EnsureDefaults(state);
                        return state;
                    }
                }
            }
            catch { }

            var defaultState = CreateInitialReferenceState();
            Save(defaultState);
            return defaultState;
        }

        public static void Save(AppPersistentState state)
        {
            try
            {
                Directory.CreateDirectory(StorageDir);
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StorageFile, json);
            }
            catch { }
        }

        private static void EnsureDefaults(AppPersistentState state)
        {
            state.Water ??= new WaterStateModel();
            state.Water.HourlyIntakeMl ??= new Dictionary<int, int>();
            state.Todos ??= new List<TodoItemModel>();
            state.ShelfItems ??= new List<ShelfItemModel>();
        }

        private static AppPersistentState CreateInitialReferenceState()
        {
            return new AppPersistentState
            {
                Water = new WaterStateModel
                {
                    DateKey = DateTime.Now.ToString("yyyy-MM-dd"),
                    CurrentLiters = 0.0,
                    GoalLiters = 1.5,
                    CupSizeLiters = 0.25,
                    RemindersEnabled = false,
                    ReminderIntervalMinutes = 60,
                    NextReminderText = DateTime.Now.AddMinutes(60).ToString("HH:mm"),
                    HourlyIntakeMl = new Dictionary<int, int>()
                },
                Todos = new List<TodoItemModel>(),
                ShelfItems = new List<ShelfItemModel>()
            };
        }
    }
}
