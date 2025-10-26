import shutil
import subprocess
from pathlib import Path

FFMPEG_PATH = Path(r"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die\Mods\1_Boombox\ffmpeg\ffmpeg-8.0-essentials_build\bin\ffmpeg.exe")
MUSIC_DIR = Path(r"C:\BoomboxMusic")
MOD_SOUNDS_DIR = Path(r"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die\Mods\1_Boombox\Sounds")

def ensure_ffmpeg() -> None:
    if not FFMPEG_PATH.exists():
        raise FileNotFoundError(f"FFmpeg not found at {FFMPEG_PATH}")


def convert_to_mono(source: Path) -> None:
    tmp_path = source.with_suffix(".tmp.wav")
    cmd = [
        str(FFMPEG_PATH),
        "-y",
        "-i",
        str(source),
        "-acodec",
        "pcm_s16le",
        "-ac",
        "1",
        "-ar",
        "44100",
        str(tmp_path),
    ]
    subprocess.run(cmd, check=True)
    tmp_path.replace(source)


def process_music_folder() -> list[Path]:
    music_dir = MUSIC_DIR
    music_dir.mkdir(parents=True, exist_ok=True)

    wav_files = sorted(music_dir.glob("*.wav"))
    mp3_files = sorted(music_dir.glob("*.mp3"))

    if mp3_files:
        for mp3 in mp3_files:
            wav_path = mp3.with_suffix(".wav")
            cmd = [
                str(FFMPEG_PATH),
                "-y",
                "-i",
                str(mp3),
                "-acodec",
                "pcm_s16le",
                "-ac",
                "1",
                "-ar",
                "44100",
                str(wav_path),
            ]
            print(f"Converting {mp3.name} -> {wav_path.name}")
            subprocess.run(cmd, check=True)

            wav_files.append(wav_path)

    if not wav_files:
        print("No audio files found in", music_dir)
        return []

    for wav in wav_files:
        print(f"Normalizing channels {wav.name}")
        convert_to_mono(wav)

    return sorted(wav_files)


def refresh_mod_soundbank(files: list[Path]) -> None:
    MOD_SOUNDS_DIR.mkdir(parents=True, exist_ok=True)

    # clear existing boombox_* files
    for existing in MOD_SOUNDS_DIR.glob("boombox_*.wav"):
        existing.unlink()

    for index, wav in enumerate(files, start=1):
        target_name = f"boombox_{index:02d}{wav.suffix.lower()}"
        target_path = MOD_SOUNDS_DIR / target_name
        print(f"Copying {wav.name} -> {target_path.name}")
        shutil.copy2(wav, target_path)


def main() -> None:
    ensure_ffmpeg()
    wav_files = process_music_folder()
    if not wav_files:
        return

    refresh_mod_soundbank(wav_files)
    print("All tracks converted to mono and copied into the mod sound bank.")


if __name__ == "__main__":
    main()
