using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TraductorVozTeams.App.Services;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App;

public partial class ProvidersWindow : Window
{
    private readonly AppServices _services;
    private readonly Dictionary<string, Func<string>> _fieldReaders = [];
    private readonly List<PasswordBox> _secretBoxes = [];
    private ProviderProfile? _current;
    private bool _isNew;
    private bool _loading;
    private bool _testing;

    public ProvidersWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        ProviderTabs.SelectedIndex = services.Settings.ActiveProfile?.ProviderType == RealtimeTranslateDescriptor.Type ? 1 : 0;
        LoadTab(services.Settings.ActiveProfile);

        Closed += (_, _) => ClearSecretBoxes();
        Closing += (_, args) => args.Cancel = _testing;
    }

    private bool IsGptTab => ProviderTabs.SelectedIndex == 1;

    private void OnProviderTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, ProviderTabs) && IsInitialized && ProviderCombo is not null && ProfileList is not null)
            LoadTab(_services.Settings.ActiveProfile);
    }

    private void LoadTab(ProviderProfile? preferred)
    {
        ProviderCombo.ItemsSource = _services.Registry.All.Where(p => (p.ProviderType == RealtimeTranslateDescriptor.Type) == IsGptTab);
        RefreshList(preferred);
    }

    private void RefreshList(ProviderProfile? select)
    {
        ProfileList.ItemsSource = null;
        var profiles = _services.Settings.Profiles.Where(p => (p.ProviderType == RealtimeTranslateDescriptor.Type) == IsGptTab).ToList();
        ProfileList.ItemsSource = profiles;
        ProfileList.SelectedItem = select is not null && profiles.Contains(select) ? select : profiles.FirstOrDefault();
        if (ProfileList.SelectedItem is null)
            StartNewProfile();
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is ProviderProfile profile)
        {
            ShowProfile(profile, isNew: false);
        }
    }

    private void OnNew(object sender, RoutedEventArgs e) => StartNewProfile();

    private void StartNewProfile()
    {
        ProfileList.SelectedItem = null;
        ShowProfile(IsGptTab
            ? new ProviderProfile { ProviderType = RealtimeTranslateDescriptor.Type, Name = "GPT Realtime Translate" }
            : new ProviderProfile { ProviderType = AzureSpeechDescriptor.Type, Name = "Azure AI Speech" }, isNew: true);
    }

    private void ShowProfile(ProviderProfile profile, bool isNew)
    {
        _loading = true;
        _current = profile;
        _isNew = isNew;
        NameBox.Text = profile.Name;
        ProviderCombo.SelectedItem = _services.Registry.Find(profile.ProviderType);
        ProviderCombo.IsEnabled = isNew && !IsGptTab;
        _loading = false;
        BuildFields();
        MessageText.Text = isNew ? "Perfil nuevo: completa los campos y pulsa Guardar." : string.Empty;
        MessageText.Foreground = Brushes.Black;
    }

    private void OnProviderTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && _current is not null && ProviderCombo.SelectedItem is IProviderDescriptor descriptor)
        {
            _current.ProviderType = descriptor.ProviderType;
            BuildFields();
        }
    }

    private void BuildFields()
    {
        ClearSecretBoxes();
        FieldsGrid.Children.Clear();
        FieldsGrid.RowDefinitions.Clear();
        _fieldReaders.Clear();
        if (_current is null || ProviderCombo.SelectedItem is not IProviderDescriptor descriptor)
        {
            return;
        }

        AvailabilityText.Text = descriptor.IsAvailable ? string.Empty : descriptor.AvailabilityNote ?? "No disponible en esta versión.";
        SaveButton.IsEnabled = descriptor.IsAvailable;
        TestButton.IsEnabled = descriptor.IsAvailable;
        GptVerificationPanel.Visibility = IsGptTab ? Visibility.Visible : Visibility.Collapsed;
        if (IsGptTab)
        {
            VerifyGptButton.IsEnabled = !_isNew && _services.Profiles.HasSecret(_current, RealtimeTranslateDescriptor.ApiKey);
            try
            {
                GptVerificationText.Text = _services.Profiles.CanStartMeeting(_current)
                    ? "Voz verificada para este perfil."
                    : "GPT no se puede usar en reuniones hasta verificar la voz en ambos sentidos. Guarda el perfil primero.";
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                GptVerificationText.Text = "No se pudo leer la verificación cifrada. Vuelve a verificar.";
            }
        }

        var row = 0;
        foreach (var field in descriptor.Fields)
        {
            FieldsGrid.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock { Text = field.Label + ":", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, ToolTip = field.Help };
            AddToGrid(label, row, 0);
            var current = _current.Values.TryGetValue(field.Key, out var v) ? v : field.DefaultValue ?? string.Empty;

            switch (field.Kind)
            {
                case ProviderFieldKind.Choice:
                    var combo = new ComboBox { ItemsSource = field.Choices, SelectedItem = current, ToolTip = field.Help, IsEnabled = descriptor.IsAvailable };
                    AddToGrid(combo, row, 1);
                    _fieldReaders[field.Key] = () => combo.SelectedItem as string ?? string.Empty;
                    break;

                case ProviderFieldKind.Secret:
                    var box = new PasswordBox { ToolTip = field.Help, IsEnabled = descriptor.IsAvailable };
                    _secretBoxes.Add(box);
                    AddToGrid(box, row, 1);
                    _fieldReaders[field.Key] = () => box.Password;
                    var saved = !_isNew && _services.Profiles.HasSecret(_current, field.Key);
                    var panel = new StackPanel { Orientation = Orientation.Horizontal };
                    panel.Children.Add(new TextBlock
                    {
                        Text = saved ? "✔ guardada" : "sin guardar",
                        Foreground = saved ? Brushes.ForestGreen : Brushes.Gray,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 4, 0),
                    });
                    var delete = new Button { Content = "Eliminar clave", IsEnabled = saved, Tag = field.Key };
                    delete.Click += OnDeleteSecret;
                    panel.Children.Add(delete);
                    AddToGrid(panel, row, 2);
                    break;

                default:
                    var text = new TextBox { Text = current, ToolTip = field.Help, IsEnabled = descriptor.IsAvailable };
                    AddToGrid(text, row, 1);
                    _fieldReaders[field.Key] = () => text.Text;
                    break;
            }

            row++;
            if (!string.IsNullOrEmpty(field.Help))
            {
                FieldsGrid.RowDefinitions.Add(new RowDefinition());
                var help = new TextBlock { Text = field.Help, Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 6) };
                AddToGrid(help, row, 1);
                Grid.SetColumnSpan(help, 2);
                row++;
            }
        }
    }

    private void AddToGrid(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        FieldsGrid.Children.Add(element);
    }

    private void ClearSecretBoxes()
    {
        foreach (var box in _secretBoxes)
        {
            box.Clear();
        }

        _secretBoxes.Clear();
    }

    private Dictionary<string, string> ReadFields() => _fieldReaders.ToDictionary(kv => kv.Key, kv => kv.Value());

    private bool TryPrepare(out Dictionary<string, string> values)
    {
        values = ReadFields();
        if (_current is null)
        {
            return false;
        }

        _current.Name = NameBox.Text.Trim();
        var errors = _services.Profiles.Validate(_current, values);
        if (errors.Count > 0)
        {
            ShowMessage(string.Join(Environment.NewLine, errors), error: true);
            return false;
        }

        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryPrepare(out var values) || _current is null)
        {
            return;
        }

        try
        {
            _services.Profiles.SaveProfile(_current, values);
            _services.Save();
            _services.Logger.Info($"Perfil guardado: {_current.Name} ({_current.ProviderType})");
            var saved = _current;
            RefreshList(saved);
            ShowProfile(saved, isNew: false);
            ShowMessage("Perfil guardado. La clave quedó cifrada fuera del ejecutable.", error: false);
        }
        catch (Exception ex)
        {
            ShowMessage("No se pudo guardar: " + ex.Message, error: true);
        }
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (!TryPrepare(out var values) || _current is null)
        {
            return;
        }

        if (!_services.Factories.TryGetValue(_current.ProviderType, out var factory))
        {
            ShowMessage("Este proveedor no está disponible en esta versión.", error: true);
            return;
        }

        var descriptor = _services.Profiles.DescriptorFor(_current);
        var plain = descriptor.Fields.Where(f => !f.IsSecret && values.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => values[f.Key].Trim());
        var typedSecrets = descriptor.Fields.Where(f => f.IsSecret && !string.IsNullOrEmpty(values.GetValueOrDefault(f.Key)))
            .ToDictionary(f => f.Key, f => values[f.Key].Trim());
        if (IsGptTab && MessageBox.Show(this, "Se abrirá una sesión en Foundry, sin micrófono ni envío de audio. " +
                "Estimación antes de redondeos: tarifa por hora × 25/3600; consulta la tarifa y el tiempo facturable de tu recurso. Continúa solo si autorizas esta prueba.",
                "Probar conexión GPT", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var context = _isNew || (IsGptTab && typedSecrets.Count > 0)
            ? new ProviderSessionContext(plain, typedSecrets, _services.Settings.SourceLanguage, _services.Settings.SegmentationSilenceMs)
            : _services.CreateContext(_current, plain, typedSecrets);

        SetTesting(true);
        ShowMessage(IsGptTab ? "Probando la sesión de Foundry sin audio…" : "Probando conexión (se envía 1 segundo de silencio)…", error: false);
        try
        {
            var result = await factory.TestConnectionAsync(context, CancellationToken.None);
            ShowMessage(result.Message, error: !result.Success);
            _services.Logger.Info($"Prueba de conexión de {_current.Name}: {(result.Success ? "correcta" : "fallida")}");
        }
        finally
        {
            SetTesting(false);
        }
    }

    private void OnDeleteSecret(object sender, RoutedEventArgs e)
    {
        if (_current is null || sender is not Button { Tag: string key })
        {
            return;
        }

        if (MessageBox.Show(this, "¿Eliminar la clave guardada de este perfil? Tendrás que escribirla de nuevo para usarlo.",
                "Eliminar clave", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _services.Profiles.DeleteSecret(_current, key);
        _services.Logger.Info($"Clave eliminada del perfil {_current.Name}");
        BuildFields();
        ShowMessage("Clave eliminada.", error: false);
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (_current is null || _isNew)
        {
            StartNewProfile();
            return;
        }

        if (MessageBox.Show(this, $"¿Eliminar el perfil \"{_current.Name}\" y sus credenciales guardadas?",
                "Eliminar perfil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _services.Profiles.DeleteProfile(_current);
        _services.Save();
        _services.Logger.Info("Perfil eliminado con sus credenciales.");
        RefreshList(_services.Settings.ActiveProfile);
        if (_services.Settings.Profiles.Count == 0)
        {
            StartNewProfile();
        }
    }

    private void ShowMessage(string text, bool error)
    {
        MessageText.Text = text;
        MessageText.Foreground = error ? Brushes.Firebrick : Brushes.ForestGreen;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void SetTesting(bool testing)
    {
        _testing = testing;
        if (Content is UIElement content)
            content.IsEnabled = !testing;
    }
}
