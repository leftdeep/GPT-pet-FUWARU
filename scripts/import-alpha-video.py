"""Import transparent MOV/WebM using the shared GPT pet converter."""
import subprocess
from pet_video_converter import main

if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.TimeoutExpired) as exc:
        raise SystemExit(str(exc))
