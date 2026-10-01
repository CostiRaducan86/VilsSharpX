import sys
import asclin_sim
from asclin_sim import AsclinModel, run

asclin_sim.MODELS = [
    AsclinModel(8, 4, 5, 3, True, 2, 'CURRENT ovs8 SP3'),
    AsclinModel(10, 1, 1, 6, True, 2, 'PROPOSED ovs10 SP6'),
]
run(sys.argv[1], 0, float(sys.argv[2]), int(sys.argv[3]))
