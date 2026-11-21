# Media support

Site compatibility and available Windows codecs vary by device and source.

- VESPER saves direct HTTP/HTTPS files, completed HLS, and supported clear DASH plans. It can merge compatible tracks into MP4, M4A, WebM, or Matroska without re-encoding them, including supported HLS video with separate packed AAC audio. Live HLS recording requires one usable rendition with audio included. HLS changes in codec or program layout can be refused to protect output integrity.
- Compatible MP4 and M4A can re-encode eligible sources using Windows Media Foundation. M4A prefers copying compatible AAC. MP3 conversion requires a confirmed Windows encoder and a supported source track. WAV uses a supported decoder and can write RF64. Opus copies supported existing Opus packets. FLAC files can be saved in their source format. FLAC encoding remains open. Some sites offer only a combined video-and-audio stream, for which the picker can decline audio extraction.
- Completed HLS/DASH trim fetches the needed segments and cuts at their boundaries. WAV trim fetches and decodes the full source before saving the selected samples. Single-file MP4 and WebM trim is unavailable.
- Tags, cover art, and chapters depend on the selected output and source metadata. Supported SRT/WebVTT tracks can be embedded in eligible MP4/Matroska outputs. Other supported captions are saved separately. Segmented and live captions have additional limits.
- Media discovery needs an accessible file or supported stream. VESPER respects DRM and site access controls.
- Chrome owns transfers whose bytes it already received. Fresh handoffs require verified identity and readiness. Page-created, POST, blob, or single-use transfers stay with Chrome when they cannot be safely replayed. Host-approved sign-in scopes credentials to the permitted origin.
- Native remux copies existing audio and video. Changing codecs requires decoding and encoding, which can take longer.
