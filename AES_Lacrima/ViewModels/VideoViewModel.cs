using AES_Core.DI;
using System.Collections.Generic;

namespace AES_Lacrima.ViewModels
{
    public interface IVideoViewModel;

    [AutoRegister]
    internal partial class VideoViewModel : MusicViewModel, IVideoViewModel
    {
        public override bool IsVideoMode => true;

        public override bool IsMetadataEditorVisible => false;

        protected override string FilePickerTitle => "Add Video Files";

        protected override string FilePickerTypeName => "Video Files";

        protected override IReadOnlyList<string> SupportedTypes => VideoSupportedTypes;

        protected override bool AllowOnlineCoverLookup => false;

    }
}