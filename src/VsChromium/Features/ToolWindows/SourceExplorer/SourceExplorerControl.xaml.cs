﻿// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.VisualStudio.ComponentModelHost;
using VsChromium.Core.Files;
using VsChromium.Core.Ipc;
using VsChromium.Core.Ipc.TypedMessages;
using VsChromium.Core.Logging;
using VsChromium.Core.Utility;
using VsChromium.Features.AutoUpdate;
using VsChromium.Package;
using VsChromium.ServerProxy;
using VsChromium.Threads;
using VsChromium.Views;
using VsChromium.Wpf;

namespace VsChromium.Features.ToolWindows.SourceExplorer {
  /// <summary>
  /// Interaction logic for SourceExplorerControl.xaml
  /// </summary>
  public partial class SourceExplorerControl : UserControl {
    // For controlling scrolling inside tree view.
    private double _treeViewHorizScrollPos;
    private bool _treeViewResetHorizScroll;
    private ScrollViewer _treeViewScrollViewer;

    private readonly IProgressBarTracker _progressBarTracker;
    private IStatusBar _statusBar;
    private ITypedRequestProcessProxy _typedRequestProcessProxy;
    private IUIRequestProcessor _uiRequestProcessor;
    private bool _swallowsRequestBringIntoView = true;
    private SourceExplorerController _controller;
    private IVisualStudioPackageProvider _visualStudioPackageProvider;
    private IFileSystemTreeSource _fileSystemTreeSource;

    public SourceExplorerControl() {
      InitializeComponent();
      // Add the "VsColors" brushes to the WPF resources of the control, so that the
      // resource keys used on the XAML file can be resolved dynamically.
      this.Resources.MergedDictionaries.Add(VsResources.BuildResourceDictionary());
      base.DataContext = new SourceExplorerViewModel();

      _progressBarTracker = new ProgressBarTracker(ProgressBar);

      //InitComboBox(SearchDirectoryNamesCombo, new ComboBoxInfo {
      //  SearchFunction = SearchDirectoryNames,
      //  PreviousElement = SearchFileNamesCombo,
      //  NextElement = SearchTextCombo,
      //});
      InitComboBox(SearchTextCombo, new ComboBoxInfo {
        SearchFunction = SearchText,
        NextElement = SearchFileNamesCombo,
      });
      InitComboBox(SearchFileNamesCombo, new ComboBoxInfo {
        SearchFunction = SearchFilesNames,
        PreviousElement = SearchTextCombo,
        NextElement = FileTreeView,
        InitialItems = {
          "*",
          "*.c;*.cpp;*.cxx;*.cc;*.tli;*.tlh;*.h;*.hh;*.hpp;*.hxx;*.hh;*.inl;*.rc;*.resx;*.idl;*.asm;*.inc",
          "*.htm;*.html;*.xml;*.gif;*.jpg;*.png;*.css;*.disco;*.js;*.srf",
          "*.xml;*.xsl;*.xslt;*.xsd;*.dtd",
          "*.txt",
          "*.cs;*.resx;*.resw;*.xsd;*.wsdl;*.xaml;*.xml;*.htm;*.html;*.css",
          "*.vb;*.resx;*.resw;*.xsd;*.wsdl;*.xaml;*.xml;*.htm;*.html;*.css",
          "*.*",
        }
      });
    }

    /// <summary>
    /// Called when Visual Studio creates our container ToolWindow.
    /// </summary>
    public void OnVsToolWindowCreated(IServiceProvider serviceProvider) {
      var componentModel = (IComponentModel)serviceProvider.GetService(typeof(SComponentModel));

      _uiRequestProcessor = componentModel.DefaultExportProvider.GetExportedValue<IUIRequestProcessor>();
      _statusBar = componentModel.DefaultExportProvider.GetExportedValue<IStatusBar>();
      _typedRequestProcessProxy = componentModel.DefaultExportProvider.GetExportedValue<ITypedRequestProcessProxy>();
      _fileSystemTreeSource = componentModel.DefaultExportProvider.GetExportedValue<IFileSystemTreeSource>();
      _visualStudioPackageProvider = componentModel.DefaultExportProvider.GetExportedValue<IVisualStudioPackageProvider>();

      _typedRequestProcessProxy.EventReceived += TypedRequestProcessProxy_EventReceived;
      _fileSystemTreeSource.TreeReceived += FileSystemTreeSourceOnTreeReceived;
      _fileSystemTreeSource.ErrorReceived += FileSystemTreeSourceOnErrorReceived;


      var standarImageSourceFactory = componentModel.DefaultExportProvider.GetExportedValue<IStandarImageSourceFactory>();
      var clipboard = componentModel.DefaultExportProvider.GetExportedValue<IClipboard>();
      var windowsExplorer = componentModel.DefaultExportProvider.GetExportedValue<IWindowsExplorer>();
      var openDocumentHelper = componentModel.DefaultExportProvider.GetExportedValue<IOpenDocumentHelper>();
      var synchronizationContextProvider = componentModel.DefaultExportProvider.GetExportedValue<ISynchronizationContextProvider>();
      _controller = new SourceExplorerController(
        this,
        _uiRequestProcessor,
        _progressBarTracker,
        standarImageSourceFactory,
        windowsExplorer,
        clipboard,
        synchronizationContextProvider,
        openDocumentHelper);

      // TODO(rpaquay): leaky abstraction. We need this because the ViewModel
      // exposes pictures from Visual Studio resources.
      ViewModel.ImageSourceFactory = standarImageSourceFactory;

      _fileSystemTreeSource.Fetch();

      // Hookup property changed notifier
      ViewModel.PropertyChanged += ViewModel_PropertyChanged;
      ViewModel.RootNodesChanged += ViewModelOnRootNodesChanged;
    }

    private void ViewModelOnRootNodesChanged(object sender, EventArgs eventArgs) {
      //FileTreeView.Items.Refresh();
      //FileTreeView.UpdateLayout();
    }

    public SourceExplorerViewModel ViewModel {
      get {
        return (SourceExplorerViewModel)DataContext;
      }
    }

    public UpdateInfo UpdateInfo {
      get { return ViewModel.UpdateInfo; }
      set { ViewModel.UpdateInfo = value; }
    }

    public ISourceExplorerController Controller {
      get { return _controller; }
    }

    private void InitComboBox(EditableComboBox comboBox, ComboBoxInfo info) {
      comboBox.DataContext = new StringListViewModel(info.InitialItems);
      comboBox.TextChanged += (s, e) => info.SearchFunction();
      comboBox.KeyDown += (s, e) => {
        if ((e.KeyboardDevice.Modifiers == ModifierKeys.None) &&
            (e.Key == Key.Return || e.Key == Key.Enter)) {
          info.SearchFunction();
        }
      };

      if (info.PreviousElement != null) {
        comboBox.PrePreviewKeyDown += (s, e) => {
          if (e.KeyboardDevice.Modifiers == ModifierKeys.None && e.Key == Key.Up) {
            if (!comboBox.IsDropDownOpen) {
              info.PreviousElement.Focus();
              e.Handled = true;
            }
          }
        };
      }

      if (info.NextElement != null) {
        comboBox.PrePreviewKeyDown += (s, e) => {
          if (e.KeyboardDevice.Modifiers == ModifierKeys.None && e.Key == Key.Down) {
            if (!comboBox.IsDropDownOpen) {
              info.NextElement.Focus();
              e.Handled = true;
            }
          }
        };
      }
    }

    private void SearchFilesNames() {
      if (string.IsNullOrWhiteSpace(SearchTextCombo.Text))
        Controller.SearchFilesNames(SearchFileNamesCombo.Text);
      else
        Controller.SearchText(SearchTextCombo.Text, SearchFileNamesCombo.Text);
    }

    private void SearchDirectoryNames() {
      Controller.SearchDirectoryNames(SearchDirectoryNamesCombo.Text);
    }

    private void SearchText() {
      SearchFilesNames();
    }

    private void TypedRequestProcessProxy_EventReceived(TypedEvent typedEvent) {
      DispatchFileSystemTreeComputing(typedEvent);
      DispatchFileSystemTreeComputed(typedEvent);
      DispatchSearchEngineFilesLoading(typedEvent);
      DispatchSearchEngineFilesLoaded(typedEvent);
      DispatchProgressReport(typedEvent);
    }

    private void DispatchProgressReport(TypedEvent typedEvent) {
      var @event = typedEvent as ProgressReportEvent;
      if (@event != null) {
        WpfUtilities.Post(this, () =>
          _statusBar.ReportProgress(@event.DisplayText, @event.Completed, @event.Total));
      }
    }

    private void DispatchFileSystemTreeComputing(TypedEvent typedEvent) {
      var @event = typedEvent as FileSystemTreeComputing;
      if (@event != null) {
        WpfUtilities.Post(this, () => {
          Logger.LogInfo("FileSystemTree is being computed on server.");
          _progressBarTracker.Start(OperationsIds.FileSystemTreeComputing,
                                    "Loading files and directory names from file system.");
          ViewModel.FileSystemTreeComputing();
        });
      }
    }

    private void DispatchFileSystemTreeComputed(TypedEvent typedEvent) {
      var @event = typedEvent as FileSystemTreeComputed;
      if (@event != null) {
        WpfUtilities.Post(this, () => {
          _progressBarTracker.Stop(OperationsIds.FileSystemTreeComputing);
          if (@event.Error != null) {
            Controller.SetFileSystemTreeError(@event.Error);
            return;
          }
          Logger.LogInfo("New FileSystemTree bas been computed on server: version={0}.", @event.NewVersion);
        });
      }
    }

    private void DispatchSearchEngineFilesLoading(TypedEvent typedEvent) {
      var @event = typedEvent as SearchEngineFilesLoading;
      if (@event != null) {
        Wpf.WpfUtilities.Post(this, () => {
          Logger.LogInfo("Search engine is loading files on server.");
          _progressBarTracker.Start(OperationsIds.FilesLoading, "Loading files contents from file system.");
        });
      }
    }

    private void DispatchSearchEngineFilesLoaded(TypedEvent typedEvent) {
      var @event = typedEvent as SearchEngineFilesLoaded;
      if (@event != null) {
        WpfUtilities.Post(this, () => {
          _progressBarTracker.Stop(OperationsIds.FilesLoading);
          if (@event.Error != null) {
            Controller.SetFileSystemTreeError(@event.Error);
            return;
          }
          Logger.LogInfo("Search engine is done loading files on server.");
        });
      }
    }

    private void FileSystemTreeSourceOnTreeReceived(FileSystemTree fileSystemTree) {
      WpfUtilities.Post(this, () => {
        Controller.SetFileSystemTree(fileSystemTree);
      });
    }

    private void FileSystemTreeSourceOnErrorReceived(ErrorResponse errorResponse) {
      WpfUtilities.Post(this, () => {
        Controller.SetFileSystemTreeError(errorResponse);
      });
    }

    private class ComboBoxInfo {
      public ComboBoxInfo() {
        this.InitialItems = new List<string>();
      }

      public Action SearchFunction { get; set; }
      public UIElement PreviousElement { get; set; }
      public UIElement NextElement { get; set; }
      public List<string> InitialItems { get; set; }
    }

    private static class OperationsIds {
      public const string FileSystemTreeComputing = "file-system-collecting";
      public const string FilesLoading = "files-loading";
    }

    public void SwallowsRequestBringIntoView(bool value) {
      _swallowsRequestBringIntoView = value;
    }

    #region WPF Event handlers

    void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e) {
      // Handle property change for the ViewModel.
      if (e.PropertyName == ReflectionUtils.GetPropertyName(ViewModel, x => x.MatchCase) ||
          e.PropertyName == ReflectionUtils.GetPropertyName(ViewModel, x => x.MatchWholeWord) ||
          e.PropertyName == ReflectionUtils.GetPropertyName(ViewModel, x => x.UseRegex) ||
          e.PropertyName == ReflectionUtils.GetPropertyName(ViewModel, x => x.UseRe2Regex) ||
          e.PropertyName == ReflectionUtils.GetPropertyName(ViewModel, x => x.IncludeSymLinks)) {
        RefreshSearchResults();
      }
    }

    private void RefreshSearchResults() {
      // Redo search
      switch (ViewModel.ActiveDisplay) {
        case SourceExplorerViewModel.DisplayKind.FileNameSearchResult:
          SearchFilesNames();
          break;
        case SourceExplorerViewModel.DisplayKind.DirectoryNameSearchResult:
          SearchDirectoryNames();
          break;
        case SourceExplorerViewModel.DisplayKind.TextSearchResult:
          SearchText();
          break;
      }
    }

    private void TreeViewItem_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e) {
      var tvi = sender as TreeViewItem;
      if (tvi == null)
        return;

      if (!tvi.IsSelected)
        return;

      if (Controller.ExecuteOpenCommandForItem(tvi.DataContext as TreeViewItemViewModel))
        e.Handled = true;
    }

    private void TreeViewItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) {
      if (_swallowsRequestBringIntoView) {
        // This prevents the tree view for scrolling horizontally to make the
        // selected item as visibile as possible. This is useful for
        // "SearchText", as text extracts are usually wide enough to make tree
        // view navigation annoying when they are selected.
        e.Handled = true;
        return;
      }

      // Find the scroll viewer and hook up scroll changed event handler.
      if (this._treeViewScrollViewer == null) {
        this._treeViewScrollViewer = this.FileTreeView.Template.FindName("_tv_scrollviewer_", this.FileTreeView) as ScrollViewer;
        if (_treeViewScrollViewer != null) {
          this._treeViewScrollViewer.ScrollChanged += this.TreeViewScrollViewer_ScrollChanged;
        }
      }

      // If we got a scroll viewer, remember the horizontal offset so we can
      // restore it in the scroll changed event.
      if (_treeViewScrollViewer != null) {
        this._treeViewResetHorizScroll = true;
        this._treeViewHorizScrollPos = this._treeViewScrollViewer.HorizontalOffset;
      }
      e.Handled = false;
    }

    private void TreeViewScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) {
      Debug.Assert(this._treeViewScrollViewer != null);

      if (this._treeViewResetHorizScroll) {
        this._treeViewScrollViewer.ScrollToHorizontalOffset(this._treeViewHorizScrollPos);
        this._treeViewResetHorizScroll = false;
      }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e) {
      // Open the default web browser to the update URL.
      Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
      e.Handled = true;
    }

    private void FileTreeView_OnPreviewKeyDown(object sender, KeyEventArgs e) {
      if (e.KeyboardDevice.Modifiers == ModifierKeys.None && e.Key == Key.Return) {
        e.Handled = Controller.ExecuteOpenCommandForItem(
          FileTreeView.SelectedItem as TreeViewItemViewModel);
      }
    }

    private void FileTreeView_KeyDown(object sender, KeyEventArgs e) {
      if (e.KeyboardDevice.Modifiers == ModifierKeys.None && e.Key == Key.Up) {
        // If topmost item is selected, move selection to bottom combo box
        var item = FileTreeView.SelectedItem as TreeViewItemViewModel;
        if (item != null) {
          var parent = item.ParentViewModel as RootTreeViewItemViewModel;
          if (parent != null) {
            if (item == parent.Children.FirstOrDefault()) {
              SearchFileNamesCombo.Focus();
              e.Handled = true;
            }
          }
        }
      }
    }

    /// <summary>
    /// Ensures the item right-clicked on is selected before showing the context
    /// menu.
    /// </summary>
    private void FileTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) {
      var source = e.OriginalSource as DependencyObject;
      if (source == null)
        return;

      var treeViewItem = WpfUtilities.VisualTreeGetParentOfType<TreeViewItem>(source);
      if (treeViewItem == null)
        return;

      treeViewItem.Focus();
      e.Handled = true;
    }

    private void SyncButton_Click(object sender, RoutedEventArgs e) {
      Logger.WrapActionInvocation(
        () => {
          var dte = _visualStudioPackageProvider.Package.DTE;
          var document = dte.ActiveDocument;
          if (document == null)
            return;
          var path = document.FullName;
          if (!PathHelpers.IsAbsolutePath(path))
            return;
          if (!PathHelpers.IsValidBclPath(path))
            return;
          Controller.ShowInSourceExplorer(new FullPath(path));
          FileTreeView.Focus();
        });
    }

    private void RefreshFileSystemTreeButton_Click(object sender, RoutedEventArgs e) {
      Logger.WrapActionInvocation(
        () => Controller.RefreshFileSystemTree());
    }

    private void GotoPrevious_Click(object sender, RoutedEventArgs e) {
      Controller.NavigateToPreviousLocation();
    }

    private void GotoNext_Click(object sender, RoutedEventArgs e) {
      Controller.NavigateToNextLocation();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) {
      Controller.CancelSearch();
    }

    private void SearchFileNames_Click(object sender, RoutedEventArgs e) {
      SearchFilesNames();
    }

    private void ClearFileNamesPattern_Click(object sender, RoutedEventArgs e) {
      SearchFileNamesCombo.Text = "";
      RefreshSearchResults();
    }

    private void SearchText_Click(object sender, RoutedEventArgs e) {
      SearchText();
    }

    private void ClearSearchText_Click(object sender, RoutedEventArgs e) {
      SearchTextCombo.Text = "";
      RefreshSearchResults();
    }

    #endregion

  }
}
