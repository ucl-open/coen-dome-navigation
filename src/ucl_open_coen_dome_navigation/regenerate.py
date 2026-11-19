from pathlib import Path

from open_rigs.rigs import conditioned_task as ct

from ucl_open_coen_dome_navigation.task import UclOpenCoenDomeNavigationTaskLogic, UclOpenCoenDomeNavigationTaskParameters

SCHEMA_ROOT = Path("./src/DataSchemas/")


def main():
    """Regenerate TaskSchema.Generated.cs, the task's JSON schema and its Bonsai.Sgen C# classes from its pydantic model (the rig's are lib-open-rigs' OpenRigs.Devices.Rigs)."""
    ct.regenerate(UclOpenCoenDomeNavigationTaskLogic, UclOpenCoenDomeNavigationTaskParameters, SCHEMA_ROOT / "ucl_open_coen_dome_navigation_task.json")


if __name__ == "__main__":
    main()
