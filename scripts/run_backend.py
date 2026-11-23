"""Launch the MazeRobot FastAPI planner backend for local Unity Live demos."""

import argparse
import enum
import os
import sys


def ensure_strenum_compat() -> None:
    """Allow running unmodified planner_service (uses enum.StrEnum) on Python 3.10 runtimes."""
    if not hasattr(enum, "StrEnum"):
        class _StrEnum(str, enum.Enum):
            def __str__(self) -> str:
                return str(self.value)

        enum.StrEnum = _StrEnum


def main() -> None:
    parser = argparse.ArgumentParser(description="Start the MazeRobot planner backend.")
    parser.add_argument(
        "--repo",
        default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        help="Repository root containing planner_service/",
    )
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    args = parser.parse_args()

    repo_root = os.path.abspath(args.repo)
    if repo_root not in sys.path:
        sys.path.insert(0, repo_root)

    os.environ.setdefault("PLANNER_EXPLORATION", "known_tasks")
    ensure_strenum_compat()

    import uvicorn
    from planner_service.app import app

    uvicorn.run(app, host=args.host, port=args.port, log_level="info")


if __name__ == "__main__":
    main()
