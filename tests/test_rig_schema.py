import json
from pathlib import Path

import pydantic
import yaml

from ucl_open_coen_av_corridor.rig import UclOpenCoenAvCorridorRig

ROOT = Path(__file__).parent.parent
TEMPLATE = ROOT / "config" / "rig-template.yml"


def test_the_rig_template_validates():
    # The template is av-dome's (pip/main-switchover): every rig's rig.yml is read by both workflows.
    rig = UclOpenCoenAvCorridorRig.model_validate(yaml.safe_load(TEMPLATE.read_text(encoding="utf-8")))
    assert rig.reward_valves.left.value == "DO1"


def test_the_committed_schema_is_the_model_s():
    # The generated C# is made from this file and refuses a rig.yml key it lacks.
    # Regenerate with `uv run regenerate-schemas`.
    committed = json.loads((ROOT / "src" / "DataSchemas" / "ucl_open_coen_av_corridor.json").read_text(encoding="utf-8"))
    model = pydantic.RootModel[UclOpenCoenAvCorridorRig].model_json_schema(
        by_alias=True, mode="serialization", union_format="primitive_type_array"
    )
    assert committed == json.loads(json.dumps(model))
