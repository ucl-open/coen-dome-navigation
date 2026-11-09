from typing import Literal, Dict
from pydantic import Field

from open_rigs.core.rig import Rig

from ucl_open_coen_av_corridor import __semver__


class UclOpenCoenAvCorridorRig(Rig):
    version: Literal[__semver__] = __semver__
    ...