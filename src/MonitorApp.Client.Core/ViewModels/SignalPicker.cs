using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>Subject and signal pickers fed by the node's catalog. "(all)" means no filter.</summary>
public sealed partial class SignalPicker : ObservableObject
{
    public const string All = "(all)";

    private IReadOnlyList<CatalogSubjectDto> subjects = [];

    [ObservableProperty]
    private string selectedSubject = All;

    [ObservableProperty]
    private string selectedSignal = All;

    public ObservableCollection<string> Subjects { get; } = [All];

    public ObservableCollection<string> Signals { get; } = [All];

    public string? SubjectFilter => SelectedSubject == All ? null : SelectedSubject;

    public string? SignalFilter => SelectedSignal == All ? null : SelectedSignal;

    /// <summary>Unit of the selected signal when exactly one subject and signal are selected and the catalog knows it.</summary>
    public string? SelectedUnit => subjects
        .FirstOrDefault(subject => subject.SubjectEntityId == SubjectFilter)?
        .Signals.FirstOrDefault(signal => signal.SignalId == SignalFilter)?.Unit;

    public void Load(CatalogDto catalog)
    {
        subjects = catalog.Subjects;
        var previousSubject = SelectedSubject;
        var previousSignal = SelectedSignal;
        Subjects.Clear();
        Subjects.Add(All);
        foreach (var subject in catalog.Subjects)
        {
            Subjects.Add(subject.SubjectEntityId);
        }

        SelectedSubject = Subjects.Contains(previousSubject) ? previousSubject : All;
        RebuildSignals();
        SelectedSignal = Signals.Contains(previousSignal) ? previousSignal : All;
    }

    partial void OnSelectedSubjectChanged(string value)
    {
        RebuildSignals();
        SelectedSignal = All;
    }

    private void RebuildSignals()
    {
        Signals.Clear();
        Signals.Add(All);
        IEnumerable<string> ids = SubjectFilter is null
            ? subjects.SelectMany(subject => subject.Signals).Select(signal => signal.SignalId).Distinct(StringComparer.Ordinal)
            : subjects.Where(subject => subject.SubjectEntityId == SubjectFilter)
                .SelectMany(subject => subject.Signals).Select(signal => signal.SignalId);
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            Signals.Add(id);
        }
    }
}
