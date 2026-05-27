using System.ComponentModel;

namespace KgmExporter;

public sealed class BotSlotViewModel : INotifyPropertyChanged
{
    public int Slot { get; }
    private string _text;

    public BotSlotViewModel(int slot)
    {
        Slot = slot;
        _text = $"[bot {slot + 1}] idle";
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
