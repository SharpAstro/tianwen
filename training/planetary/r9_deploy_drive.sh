#!/usr/bin/env bash
# R9 L-GAINS-D's readings as the twins come: step 0 on each finished twin; once all 30 are made, the comparators, step 1, G6 and G5.
cd D:/temp/tianwen-scratch/i1056/deploy
until grep -q "all deploy twins" deploy.done; do python deploy_steps.py step0 >> step0.log 2>&1; sleep 120; done
python deploy_steps.py step0 >> step0.log 2>&1; echo "step0 done" >> drive.done
python deploy_steps.py comparators > comparators.log 2>&1; echo "comparators done $?" >> drive.done
python deploy_steps.py step1 > step1.log 2>&1; echo "step1 done $?" >> drive.done
python deploy_steps.py real > real.log 2>&1; echo "real done $?" >> drive.done
