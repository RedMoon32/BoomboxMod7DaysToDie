import subprocess
from pathlib import Path

ffmpeg_path = Path(r"C:\\Program Files (x86)\\Steam\\steamapps\\common\\7 Days To Die\\Mods\\1_Boombox\\ffmpeg\\ffmpeg-8.0-essentials_build\\bin\\ffmpeg.exe")
music_dir = Path(r"C:\\BoomboxMusic")

if not ffmpeg_path.exists():
    raise FileNotFoundError(f"FFmpeg not found at {ffmpeg_path}")

music_dir.mkdir(parents=True, exist_ok=True)

mp3_files = list(music_dir.glob("*.mp3"))
if not mp3_files:
    print("No MP3 files found in", music_dir)
else:
    for mp3 in mp3_files:
        wav_path = mp3.with_suffix(".wav")
        cmd = [str(ffmpeg_path), "-y", "-i", str(mp3), "-acodec", "pcm_s16le", "-ar", "44100", str(wav_path)]
        print("Converting", mp3.name, "->", wav_path.name)
        subprocess.run(cmd, check=True)
    print("Conversion complete.")
