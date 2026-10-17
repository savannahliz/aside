using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QuietPin;

public sealed class Item
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Pinned { get; set; }
    public bool Completed { get; set; }
    public int PinOrder { get; set; }
}

public sealed class Preferences
{
    public string Background { get; set; } = "#FFE8E6DB";
    public double IdleOpacity { get; set; } = .40;
    public double ActiveOpacity { get; set; } = .95;
    public bool Fade { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public bool Collapsed { get; set; }
    public int Shortcut { get; set; }
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double ExpandedWidth { get; set; } = 360;
    public double ExpandedHeight { get; set; } = 460;
    public double CompactWidth { get; set; } = 330;
    public double CompactHeight { get; set; } = 170;
    public bool StripMode { get; set; }
    public double StripWidth { get; set; } = 330;
    public string? DockEdge { get; set; }
    public uint? CustomKey { get; set; }
    public uint? CustomModifiers { get; set; }
    public string? CustomKeyLabel { get; set; }
    public bool ControlEnterToSave { get; set; }
    public double CaptureOpacity { get; set; } = .95;
    public List<string> SavedColors { get; set; } = new();
    public bool SkipClearCompletedConfirmation { get; set; }
    public int PinLimit { get; set; } = 3;
    [System.Text.Json.Serialization.JsonIgnore]
    public int PinCapacity => Math.Clamp(PinLimit, 1, 20);
}

public sealed class Notebook
{
    public List<Item> Items { get; set; } = new();
    public Preferences Preferences { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public Item[] Pins => Items.Where(i => i.Pinned && !i.Completed).OrderBy(i => i.PinOrder).ToArray();

    public Guid? Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var item = new Item { Content = text.Trim() };
        Items.Add(item);
        return item.Id;
    }

    public bool Pin(Guid id, Guid? replace = null)
    {
        var item = Items.Find(i => i.Id == id && !i.Completed);
        if (item == null) return false;
        if (item.Pinned) return true;
        int rank;
        if (replace.HasValue)
        {
            var old = Items.Find(i => i.Id == replace && i.Pinned && !i.Completed);
            if (old == null) return false;
            rank = old.PinOrder;
            old.Pinned = false;
        }
        else
        {
            if (Pins.Length >= Preferences.PinCapacity) return false;
            rank = Pins.Length;
        }
        item.Pinned = true;
        item.PinOrder = rank;
        Normalize();
        return true;
    }

    public void Unpin(Guid id) { var i = Items.Find(i => i.Id == id); if (i != null) i.Pinned = false; Normalize(); }
    public bool Edit(Guid id, string content)
    {
        var item = Items.Find(i => i.Id == id);
        if (item == null || string.IsNullOrWhiteSpace(content)) return false;
        item.Content = content.Trim();
        return true;
    }
    public void Complete(Guid id)
    {
        var i = Items.Find(i => i.Id == id);
        if (i == null) return;
        i.Completed = !i.Completed;
        i.Pinned = false;
        Normalize();
    }
    public void Delete(Guid id) { Items.RemoveAll(i => i.Id == id); Normalize(); }
    public void ClearCompleted(bool skipFutureConfirmation = false)
    {
        Items.RemoveAll(i => i.Completed);
        if (skipFutureConfirmation) Preferences.SkipClearCompletedConfirmation = true;
    }
    public void Move(Guid id, Guid target)
    {
        var order = Pins.ToList();
        var source = order.FindIndex(i => i.Id == id);
        var destination = order.FindIndex(i => i.Id == target);
        if (source < 0 || destination < 0 || source == destination) return;
        var item = order[source]; order.RemoveAt(source); order.Insert(destination, item);
        for (var i = 0; i < order.Count; i++) order[i].PinOrder = i;
    }
    public void Normalize() { var pins = Pins; for (var i = 0; i < pins.Length; i++) pins[i].PinOrder = i; }
    public void Validate()
    {
        if (Items == null || Preferences == null || Items.Any(i => i == null || i.Content == null) ||
            Items.Select(i => i.Id).Distinct().Count() != Items.Count || Pins.Length > 20 || Items.Any(i => i.Completed && i.Pinned))
            throw new InvalidDataException("记录文件格式不正确");
        Normalize();
    }
}

public sealed class Store
{
    private readonly string path;
    private readonly bool writable;
    public Notebook Book { get; private set; } = new();
    public string? LoadError { get; }
    // True on the very first launch, before any notes or settings have been saved.
    public bool FirstLaunch { get; }
    private static readonly JsonSerializerOptions options = new() { WriteIndented = true };
    public Store(string directory)
    {
        path = Path.Combine(directory, "inbox.json");
        FirstLaunch = !File.Exists(path);
        try
        {
            if (File.Exists(path)) Book = JsonSerializer.Deserialize<Notebook>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            Book.Validate(); writable = true;
        }
        catch (Exception ex) { LoadError = "无法读取记录，已停止写入以保护原文件。\n" + path + "\n" + ex.Message; }
    }
    public void Edit(Action<Notebook> action)
    {
        if (!writable) throw new IOException(LoadError);
        var next = JsonSerializer.Deserialize<Notebook>(JsonSerializer.Serialize(Book, options))!;
        action(next);
        next.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next, options));
        File.Move(temp, path, true);
        Book = next;
    }
}
