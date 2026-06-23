using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ExtremeSignalAppCS.Models
{
    public class IntervalStat : ObservableObject
    {

        private string _intervalName = "";
        private int _shortCount;
        private int _longCount;
        private string _displayText = "";
        private string _displayColor = "#DCDCDC";

        public string IntervalName { get => _intervalName; set => SetField(ref _intervalName, value); }
        public int ShortCount { get => _shortCount; set => SetField(ref _shortCount, value); }
        public int LongCount { get => _longCount; set => SetField(ref _longCount, value); }
        
        public string DisplayText { get => _displayText; set => SetField(ref _displayText, value); }
        public string DisplayColor { get => _displayColor; set => SetField(ref _displayColor, value); }
    }
}
