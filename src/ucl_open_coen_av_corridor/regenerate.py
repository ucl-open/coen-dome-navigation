from pathlib import Path
from typing import Union
import json
import pydantic
import os

from open_rigs.core import ExperimentSession
import ucl_open_coen_av_corridor.rig
import ucl_open_coen_av_corridor.task

SCHEMA_ROOT = Path("./src/DataSchemas/")
SCHEMA_FILE = SCHEMA_ROOT / "ucl_open_coen_av_corridor.json"


def main():
    models = [
        ucl_open_coen_av_corridor.task.UclOpenCoenAvCorridorTaskLogic,
        ucl_open_coen_av_corridor.rig.UclOpenCoenAvCorridorRig,
        ExperimentSession
    ]
    model = pydantic.RootModel[Union[tuple(models)]]
    schema = model.model_json_schema(by_alias=True, mode="serialization", union_format="primitive_type_array")
    SCHEMA_ROOT.mkdir(parents=True, exist_ok=True)
    SCHEMA_FILE.write_text(json.dumps(schema, indent=2))
    print(f"Schema written to {SCHEMA_FILE}")
    os.system("dotnet bonsai.sgen src/DataSchemas/ucl_open_coen_av_corridor.json --output src/Extensions --serializer json --serializer yaml")


if __name__ == "__main__":
    main()
