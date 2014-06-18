﻿//* Copyright 2010-2011 Research In Motion Limited.
//*
//* Licensed under the Apache License, Version 2.0 (the "License");
//* you may not use this file except in compliance with the License.
//* You may obtain a copy of the License at
//*
//* http://www.apache.org/licenses/LICENSE-2.0
//*
//* Unless required by applicable law or agreed to in writing, software
//* distributed under the License is distributed on an "AS IS" BASIS,
//* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//* See the License for the specific language governing permissions and
//* limitations under the License.

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlackBerry.Package.Model;
using BlackBerry.Package.ViewModels;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace BlackBerry.Package.Editors
{
    /// <summary>
    /// Interaction logic for BarDescription editor control.
    /// </summary>
    public partial class BarDescriptorEditorControl : UserControl
    {
        private readonly ServiceProvider _serviceProvider;
        /// <summary>
        /// Primary Constructor
        /// </summary>
        public BarDescriptorEditorControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Overloaded Constructor
        /// </summary>
        /// <param name="viewModel"></param>
        public BarDescriptorEditorControl(ServiceProvider serviceProvider, IBarEditorViewModel viewModel)
        {
            _serviceProvider = serviceProvider; 
            DataContext = viewModel;
            InitializeComponent();

            // wait until we're initialized to handle events
            viewModel.ViewModelChanged += ViewModelChanged;
        }

        public IBarEditorViewModel ViewModel
        {
            get { return DataContext as IBarEditorViewModel; }
        }

        internal void DoIdle()
        {
            // only call the view model DoIdle if this control has focus
            // otherwise, we should skip and this will be called again
            // once focus is regained
            var viewModel = ViewModel;
            if (viewModel != null && IsKeyboardFocusWithin)
            {
                viewModel.DoIdle();
            }
        }

        /// <summary>
        /// ViewModel Changerd Event
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ViewModelChanged(object sender, EventArgs e)
        {
            // this gets called when the view model is updated because the Xml Document was updated
            // since we don't get individual PropertyChanged events, just re-set the DataContext
            var viewModel = ViewModel;
            DataContext = null; // first, set to null so that we see the change and rebind
            DataContext = viewModel;
        }

        /// <summary>
        /// Check to see if the inputed text is allowed
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private Boolean IsTextAllowed(String text) 
        { 
            return Array.TrueForAll(text.ToCharArray(), 
                delegate(Char c) { return Char.IsDigit(c) || Char.IsControl(c); }); 
        }     
        
        /// <summary>
        /// Use the PreviewTextInputHandler to respond to key presses
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
 
        private void PreviewTextInputHandler(object sender, TextCompositionEventArgs e) 
        { 
            e.Handled = !IsTextAllowed(e.Text); 
        } 

        /// <summary>
        /// Use the DataObject.Pasting Handler 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PastingHandler(object sender, DataObjectPastingEventArgs e) 
        { 
            if (e.DataObject.GetDataPresent(typeof(String))) 
            { 
                String text = (String)e.DataObject.GetData(typeof(String)); 
                if (!IsTextAllowed(text)) e.CancelCommand(); 
            } 
            else e.CancelCommand(); 
        }

        /// <summary>
        /// GetAuthor Button Click Event
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btGetAuthor_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = ViewModel;

            if (viewModel != null)
            {
                viewModel.setAuthorInfo();
            }
        }

        private void btnAddIC_Click(object sender, RoutedEventArgs e)
        {
            string filename = string.Empty;
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.DefaultExt = ".png"; // Default file extension
            dlg.Filter = "Image Files (*.png, *.jpg, *.gif)|*.png;*.jpg;*.gif"; 
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                filename = dlg.FileName;

                AddIconToProject(filename);

                var viewModel = ViewModel;
                if (viewModel != null)
                {
                    FileInfo fileInfo = new FileInfo(filename);
                    viewModel.AddIcon(fileInfo);
                }
            }
        }

        private void AddIconToProject(string path)
        {
            try
            {
                DTE dte = (DTE)_serviceProvider.GetService(typeof(DTE));
                string solutionDir = Path.GetDirectoryName(dte.Solution.FullName);


                Projects projs = dte.Solution.Projects;
                foreach (Project proj in projs)
                {
                    var viewModel = ViewModel;
                    if (viewModel != null)
                    {

                        FileInfo fileInfo1 = new FileInfo(proj.FullName);
                        FileInfo fileInfo2 = new FileInfo(viewModel.Model.Name);
                        FileInfo fileInfo3 = new FileInfo(path);

                        if (viewModel.AppName == proj.Name)
                        {
                            proj.ProjectItems.AddFromFile(path);
                            viewModel.AddLocalAsset(path);
                        }
                    }
                }
            }
            catch (Exception)
            {

            }
        }

        private void btnRemoveIC_Click(object sender, RoutedEventArgs e)
        {
            if (grdIcon.SelectedItem != null)
            {
                var viewModel = ViewModel;
                if (viewModel != null)
                {
                    viewModel.DeleteIcon(grdIcon.SelectedItem);
                }
            }
        }


        private void btnAddSS_Click(object sender, RoutedEventArgs e)
        {
            string filename = string.Empty;
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.DefaultExt = ".png"; // Default file extension
            dlg.Filter = "Image Files (*.png, *.jpg, *.gif)|*.png;*.jpg;*.gif"; 
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                filename = dlg.FileName;

                AddIconToProject(filename);

                var viewModel = ViewModel;
                if (viewModel != null)
                {
                    FileInfo fileInfo = new FileInfo(filename);
                    viewModel.AddSplashScreen(fileInfo);
                }
            }

        }

        private void btnRemoveSS_Click(object sender, RoutedEventArgs e)
        {
            if (grdSplashScreen.SelectedItem != null)
            {
                var viewModel = ViewModel;
                if (viewModel != null)
                {
                    viewModel.DeleteSplashScreen(grdSplashScreen.SelectedItem);
                }
            }
        }

        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as CheckBox).IsChecked == true)
            {
                var viewModel = ViewModel;
                if (viewModel != null)
                {
                    viewModel.CheckPermission((grdPermissions.SelectedItem as PermissionItemClass).Identifier);
                };
            }
            else
            {
                var viewModel = ViewModel;
                if (viewModel != null)
                {
                    viewModel.UnCheckPermission((grdPermissions.SelectedItem as PermissionItemClass).Identifier);
                }
            }

        }

        private void btnAddAsset_Click(object sender, RoutedEventArgs e)
        {
            string filename = string.Empty;
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();

            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                filename = dlg.FileName;

                var viewModel = ViewModel;
                if (viewModel != null)
                {
                    viewModel.AddLocalAsset(filename);
                }
            }
        }

        private void btnRemoveAsset_Click(object sender, RoutedEventArgs e)
        {
            if (grdAssets.SelectedItem != null)
            {
                var viewModel = ViewModel;
                if (viewModel != null)
                {
                    viewModel.DeleteLocalAsset(grdAssets.SelectedItem);
                }
            }
        }

        private void CheckBox_Click_1(object sender, RoutedEventArgs e)
        {
            var viewModel = ViewModel;
            if (viewModel != null)
            {
                bool isPublic = (sender as CheckBox).IsChecked == true;
                viewModel.EditLocalAsset((grdAssets.SelectedItem as asset).Value, isPublic, "");
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string assetType;

            if (e.RemovedItems.Count != 0)
            {
                if (grdAssets.SelectedItems.Count != 0)
                {
                    var viewModel = ViewModel;
                    if (viewModel != null)
                    {
                        assetType = (sender as ComboBox).SelectedValue.ToString();
                        viewModel.EditLocalAsset((grdAssets.SelectedItem as asset).Value, null, assetType);
                    }
                }
            }
        }
    }
}
