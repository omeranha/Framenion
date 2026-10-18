using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace framenion.Src;

/// <summary>
/// An observable collection that can swap its whole contents with a single Reset notification.
/// The grids bind to one instance for the lifetime of the window: reassigning ItemsSource does
/// not reliably re-realize an ItemsRepeater, and Clear() followed by N Add() calls makes the
/// repeater re-measure once per item.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
	public void Replace(IEnumerable<T> items)
	{
		CheckReentrancy();

		Items.Clear();
		foreach (var item in items) {
			Items.Add(item);
		}

		OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
		OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
		OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
	}
}
