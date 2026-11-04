/*==========================================================*/
// Copyright  The Skymu Team and other contributors.
// For any inquiries or concerns, email contact@skymu.app.
/*==========================================================*/
// Modification or redistribution of this code is governed
// by the terms set out in the project license agreement.
// If you do not comply with those terms, you may not
// modify or distribute any original code from the project.
/*==========================================================*/
// License: https://skymu.app/legal/license
// SPDX-License-Identifier: AGPL-3.0-or-later
/*==========================================================*/
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Yggdrasil.Enumerations;
using Yggdrasil.Models;

namespace Skymu.Converters
{
    // Mirrors NullDependentVisibilityConverter's image logic but inverted:
    // shows the generic "file attachment" row for attachments that are NOT
    // a renderable image (files, videos, audio) rather than hiding them.
    public class FileAttachmentVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Attachment[] attachments && attachments.Length > 0)
            {
                var type = attachments[0].Type;
                if (type != AttachmentType.Image && type != AttachmentType.ThumbnailImage)
                    return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
