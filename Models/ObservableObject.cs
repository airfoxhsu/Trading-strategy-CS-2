using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 提供 INotifyPropertyChanged 的基礎實作，
    /// 讓子類別能透過 SetField 輕鬆實作屬性變更通知。
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
