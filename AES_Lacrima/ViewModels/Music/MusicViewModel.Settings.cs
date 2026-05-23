using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Code.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Lacrima.Settings;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using log4net;
using TagLib;


namespace AES_Lacrima.ViewModels
{
    public partial class MusicViewModel : ViewModelBase, IMusicViewModel 
    {
        #region Everything Else
        // Everything Else
        protected override string SettingsFilePath => ApplicationPaths.GetSettingsFile("Playlist.json");

        protected override void OnLoadSettings(JsonObject section)
        {
            AudioPlayer?.Volume = ReadDoubleSetting(section, "Volume", DefaultPersistedVolume);
            IsAlbumlistOpen = ReadBoolSetting(section, nameof(IsAlbumlistOpen));
            Log.Info("MusicViewModel.OnLoadSettings applied lightweight playlist state on the UI thread.");
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            if (AudioPlayer != null) WriteSetting(section, "Volume", AudioPlayer.Volume);
            WriteSetting(section, nameof(IsAlbumlistOpen), IsAlbumlistOpen);
            WriteCollectionSetting(section, nameof(AlbumList), "FolderMediaItem", GetAlbumsForPersistence());
        }
        #endregion
    }
}
