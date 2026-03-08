using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using WebApplication1.Enums;

namespace MauiApp1.Model
{
    public class RouteDto //: INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string CarSerialNumber { get; set; }
        public string Name { get; set; }
        public string Start { get; set; }
        public string End { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public Status Status { get; set; }

        //bool _isExpanded;
        //public bool IsExpanded
        //{
        //    get => _isExpanded;
        //    set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
        //}

        //public event PropertyChangedEventHandler? PropertyChanged;
        //void OnPropertyChanged([CallerMemberName] string? n = null) =>
        //    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public ObservableCollection<GpsLogEntry> Points { get; set; } = new();
    }
}
