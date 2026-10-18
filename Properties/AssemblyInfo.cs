using Microsoft.VisualBasic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// [MANDATORY] The following GUID is used as a unique identifier of the plugin. Generate a fresh one for your plugin!
[assembly: Guid("f3fd7bb5-2b69-40cc-846f-4f4a2ff62518")]

// [MANDATORY] The assembly versioning
//Should be incremented for each new release build of a plugin
[assembly: AssemblyVersion("1.0.0.5")]
[assembly: AssemblyFileVersion("1.0.0.5")]

// [MANDATORY] The name of your plugin
[assembly: AssemblyTitle("Sony Camera Plugin")]
// [MANDATORY] A short description of your plugin
[assembly: AssemblyDescription("Community-maintained Sony camera and lens support; originally developed by @dougforpres")]

// The following attributes are not required for the plugin per se, but are required by the official manifest meta data

// Your name
[assembly: AssemblyCompany("@dougforpres")]
// The product name that this plugin is part of
[assembly: AssemblyProduct("Sony Camera Plugin")]
[assembly: AssemblyCopyright("Copyright © 2023-2024 Doug Henderson")]

// The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.3001")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
// The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/shurkanTwo/NINASonyCameraPlugin")]

// The following attributes are optional for the official manifest meta data

//[Optional] Your plugin homepage URL - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://github.com/shurkanTwo/NINASonyCameraPlugin")]

//[Optional] Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Sony,Camera")]

//[Optional] A link that will show a log of all changes in between your plugin's versions
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/shurkanTwo/NINASonyCameraPlugin/blob/master/CHANGELOG.md")]

//[Optional] The url to a featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "https://retro.kiwi/images/NINAPluginLogoV2.jpg")]
//[Optional] A url to an example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "")]
//[Optional] An additional url to an example example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
//[Optional] An in-depth description of your plugin
[assembly: AssemblyMetadata("LongDescription", @"Community-maintained fork of [@dougforpres’s original plugin](https://github.com/dougforpres/NINASonyCameraPlugin), maintained by @shurkanTwo and contributors.

[Manual installation](https://github.com/shurkanTwo/NINASonyCameraPlugin/blob/master/INSTALLATION.md).

## Camera
* Support for many different Sony Cameras. [See the list](https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Supported-Cameras)
* Provides LiveView function if the camera supports it
* Images are downloaded in Sony's native ARW format (this is a N.I.N.A feature) so full meta-data is available
* ISO is controllable via Gain property
* The driver selects the nearest learnt shutter speed, or verified BULB mode for longer exposures

## Focuser
* Support for over 15 different camera-mounted lenses. [See the list](https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Supported-Lenses)

*Note*: This addon uses the core (non-ASCOM) files present in the Sony ASCOM driver v1.0.1.17 and later, which must be installed for this addon to start up correctly. You can find the Sony ASCOM driver at [https://github.com/dougforpres/ASCOMSonyCameraDriver/releases](https://github.com/dougforpres/ASCOMSonyCameraDriver/releases).

## Support
Report problems with this fork in [its issue tracker](https://github.com/shurkanTwo/NINASonyCameraPlugin/issues). @dougforpres remains the original author; his homepage is [retro.kiwi](https://retro.kiwi).

For many bugs/problems I'm going to ask you for a driver log file, [here's a link to how you would do that](https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Troubleshooting#the-driver-dll-log).

* [My camera is not supported](https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Installation#if-you-dont-have-a-supported-camera-model)
* [List of supported cameras](https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Supported-Cameras)
* [Other known issues](https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Troubleshooting)

## Contributors
* [@shurkanTwo](https://github.com/shurkanTwo)
* [@totoantibes](https://github.com/totoantibes)
* [@christian-photo](https://github.com/christian-photo)
* [@naixx](https://github.com/naixx)
")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]
// [Unused]
[assembly: AssemblyConfiguration("")]
// [Unused]
[assembly: AssemblyTrademark("")]
// [Unused]
[assembly: AssemblyCulture("")]
