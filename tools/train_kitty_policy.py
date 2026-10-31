# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0
"""Reproduce Relay's original recurrent behavior and attention policy.

Development-only dependency: NumPy. The app needs no Python, model runtime,
network, user recordings, or pretrained model. All examples are synthetic.
22 inputs -> 24 recurrent tanh units -> 32 tanh -> 24 tanh -> 25 behavior
and 5 attention softmax outputs. All 3,470 parameters are trained, including
the temporal recurrence, using backpropagation through synthetic episodes.
Held-out agreement measures imitation of the synthetic teacher, not sentience.
"""
import os
os.environ.setdefault('OPENBLAS_NUM_THREADS', '1')
from pathlib import Path
import numpy as np

SEED = 20261031
NAMES = ('Watch','Walk','Chase','Swat','SmackPanel','Zoomies','Groom','Sleep','Grumpy','Stretch',
         'Sniff','Knead','Loaf','SlowBlink','LookAround','Stalk','Pounce','Rub','Scratch','Inspect',
         'Yawn','Chirp','Meow','Roll','Circle')

def softmax(x):
    x = x-x.max(axis=-1,keepdims=True)
    x = np.exp(x)
    return x/x.sum(axis=-1,keepdims=True)

def teacher(x, trace):
    e,s,i,a,n,m,c,idle,p,aff,age,roam,cur,trust,habit,nov,social,startle,play,rest,contact,completion = x.T
    # These traces are hidden from the current input: temporal recurrence must
    # learn to retain earlier stimulation and novelty across an episode.
    wave = n*m
    past_wave,past_near,past_novel = trace.T
    hunt = np.maximum(wave,past_wave*.75)*(1-habit*.65)
    calm = np.maximum(0,1-a-i)
    scores = np.array([
        1.4+.5*n-.8*s-a-i,
        .1+2.8*roam+.5*e-2*s-i,
        -2+4*hunt+2*a+e-2.5*s-3*i,
        -1+3.1*n+1.1*hunt+.6*a-1.5*i-1.5*habit,
        -1.9+3.3*p+roam+a-2*s-2*i,
        -3.8+8*a+1.8*e+.8*past_wave-4*i-4*s,
        -.1+1.6*idle+aff+1.4*play-1.4*roam-3.8*a-2.4*i,
        -3.1+6.5*s+2.8*(1-e)+idle-5*a-2*c-i,
        -3+7.8*i+.7*c+startle-.3*aff,
        -.9+2.7*rest+1.1*s+.8*idle-2.6*a-i,
        -1.4+3*nov+1.5*past_novel+1.1*cur+.6*p-2*a-2*i,
        -1.8+2.5*rest+2.1*trust+1.4*s+calm-2.8*a-2*i,
        -1.1+2.1*s+1.3*idle+1.3*trust+calm-2.5*a-2*i,
        -1.5+2.8*trust+1.1*aff+.8*past_near+.5*calm-2*a-2*i,
        -.1+2.1*cur+1.1*nov+habit-.5*n-1.7*s-i,
        -1.7+3.9*hunt+1.2*cur+e-a-2*i-2*s,
        -2.4+4.5*past_wave+1.5*a+e+completion-2*i-2.2*s,
        -2+2.4*trust+1.9*contact+.8*p+aff-2.5*a-3*i,
        -2+2.7*p+1.4*roam+1.2*e-1.2*s-1.5*a-2*i,
        -1.5+3*nov+2.3*cur+roam-2*a-2*i,
        -2.6+3.3*rest+2.4*s+1.1*idle-3*a-2*i,
        -2.2+2.4*past_wave+1.7*play+1.1*social+trust-2*i-2*s,
        -2.8+3.4*social+1.8*contact+trust+.4*n-2*i-1.5*s,
        -2.5+2.2*trust+aff+1.6*calm+.6*e-1.5*s-2*i,
        -2+2.8*s+2*rest+.7*roam+calm-3*a-2*i,
    ]).T
    attention = np.array([
        -1+3.2*n+2*hunt+social-3*habit-2*i-2*s,
        .5+2.2*cur+2*nov+1.2*habit-1.8*s-.7*i,
        .1+2*i+1.4*play+.8*contact+.5*calm,
        -1.2+4*s+2*(1-e)+1.5*rest-3*a,
        -.2+1.4*rest+1.1*play+1.3*past_novel+habit,
    ]).T
    return softmax(scores/.85),softmax(attention/.85)

def episodes(rng,count,length=18):
    x=np.empty((count,length,22))
    current=rng.random((count,22))
    current[:,0]=rng.uniform(.5,1,count); current[:,1:4]*=.35
    current[:,6]*=.1; current[:,9]=rng.uniform(.4,.85,count)
    current[:,12:14]=rng.uniform(.35,.9,(count,2)); current[:,14:16]*=.1
    current[:,17:22]*=.1
    scenario=rng.integers(0,7,count)
    trace=np.zeros((count,3)); labels=[]; gazes=[]
    for t in range(length):
        current=np.clip(current*.965+rng.random((count,22))*.035,0,1)
        current[:,4:6]=0; current[:,8]=rng.integers(0,2,count)
        calm=scenario==0; wave=(scenario==1)&(t>=3)&(t<12)
        pest=(scenario==2)&(t>=3)&(t<12); sleepy=scenario==3
        novel=(scenario==4)&(t>=4)&(t<10); social=scenario==5
        recovering=scenario==6
        current[calm,1]=.12+t*.013; current[calm,2:4]*=.6; current[calm,7]=t/(length-1)
        current[wave,4:6]=1; current[wave,3]=min(1,(t-2)*.12)
        current[pest,4]=1; current[pest,6]=min(1,(t-2)*.15)
        current[pest,2]=min(1,(t-2)*.12); current[pest,3]=.55
        current[sleepy,0]=.35; current[sleepy,1]=.7; current[sleepy,3]=.01; current[sleepy,19]=.7
        current[novel,15]=1; current[novel,12]=.9
        current[social,16]=.9; current[social,13]=.85; current[social,20]=.6
        current[recovering,18]=.8; current[recovering,19]=.5; current[recovering,3]=max(0,.8-t*.055)
        current[:,10]=t/(length-1); current[:,21]=(t%6)/5
        current[:,14]=np.where(current[:,4]>.2,np.minimum(1,current[:,14]+.035),current[:,14]*.94)
        current[:,15]=np.where(novel,1,current[:,15]*.8)
        trace=trace*.72+np.column_stack((current[:,4]*current[:,5],current[:,4],current[:,15]))*.28
        action,gaze=teacher(current,trace)
        x[:,t]=current; labels.append(action); gazes.append(gaze)
    return x,np.stack(labels,axis=1),np.stack(gazes,axis=1)

def forward(x,params):
    wx,wh,bh,w1,b1,w2,b2,wa,ba,wg,bg=params
    h=np.zeros((len(x),24)); cache=[]; logits=[]; gazes=[]
    for t in range(x.shape[1]):
        previous=h; h=np.tanh(x[:,t]@wx+previous@wh+bh)
        d1=np.tanh(h@w1+b1); d2=np.tanh(d1@w2+b2)
        action=softmax(d2@wa+ba); gaze=softmax(d2@wg+bg)
        cache.append((previous,h,d1,d2,action,gaze)); logits.append(action); gazes.append(gaze)
    return np.stack(logits,axis=1),np.stack(gazes,axis=1),cache

def main():
    rng=np.random.default_rng(SEED)
    x,labels,gazes=episodes(rng,2400)
    x=x*2-1
    shapes=[(22,24),(24,24),(24,),(24,32),(32,),(32,24),(24,),(24,25),(25,),(24,5),(5,)]
    params=[rng.normal(0,.14,s) if len(s)==2 else np.zeros(s) for s in shapes]
    params[1]*=.55
    first=[np.zeros_like(p) for p in params]; second=[np.zeros_like(p) for p in params]
    step=0
    for epoch in range(135):
        for indices in np.array_split(rng.permutation(len(x)),25):
            a=x[indices]; target=labels[indices]; target_gaze=gazes[indices]
            prediction,attention,cache=forward(a,params)
            grads=[np.zeros_like(p) for p in params]; future=np.zeros((len(a),24))
            wx,wh,bh,w1,b1,w2,b2,wa,ba,wg,bg=params
            for t in reversed(range(a.shape[1])):
                previous,h,d1,d2,act,gaze=cache[t]
                ea=(act-target[:,t])/(len(a)*a.shape[1])
                eg=.55*(gaze-target_gaze[:,t])/(len(a)*a.shape[1])
                grads[7]+=d2.T@ea; grads[8]+=ea.sum(0); grads[9]+=d2.T@eg; grads[10]+=eg.sum(0)
                e2=(ea@wa.T+eg@wg.T)*(1-d2*d2)
                grads[5]+=d1.T@e2; grads[6]+=e2.sum(0)
                e1=(e2@w2.T)*(1-d1*d1)
                grads[3]+=h.T@e1; grads[4]+=e1.sum(0)
                eh=(e1@w1.T+future)*(1-h*h)
                grads[0]+=a[:,t].T@eh; grads[1]+=previous.T@eh; grads[2]+=eh.sum(0)
                future=eh@wh.T
            step+=1
            for j,g in enumerate(grads):
                np.clip(g,-1,1,out=g)
                first[j]=.9*first[j]+.1*g; second[j]=.999*second[j]+.001*g*g
                params[j]-=.004*(first[j]/(1-.9**step))/(np.sqrt(second[j]/(1-.999**step))+1e-8)
        if epoch%45==44: print(f'Epoch {epoch+1}/135',flush=True)
    validation,targets,attention_targets=episodes(np.random.default_rng(SEED+1),500)
    actions,attention,_=forward(validation*2-1,params)
    agreement=np.mean(actions.argmax(-1)==targets.argmax(-1))
    gaze_agreement=np.mean(attention.argmax(-1)==attention_targets.argmax(-1))
    assert agreement>.85 and gaze_agreement>.85,(agreement,gaze_agreement)
    names=('InputWeights','RecurrentWeights','RecurrentBias','DenseWeights','DenseBias','DeepWeights','DeepBias','OutputWeights','OutputBias','AttentionWeights','AttentionBias')
    count=sum(p.size for p in params)
    assert count==3470,count
    lines=['// Copyright (c) 2026 Frame Bridge contributors.','// SPDX-License-Identifier: Apache-2.0',
           '// Generated by tools/train_kitty_policy.py; original synthetic temporal episodes only.',
           f'// Seed {SEED}; 22 -> recurrent24 -> dense32 -> dense24 -> behavior25 + attention5; {count} parameters.',
           f'// Held-out synthetic agreement: behavior {agreement:.2%}, attention {gaze_agreement:.2%} (500 episodes / 9,000 timesteps).',
           'namespace BabbleFrameBridge.App;','internal static class KittyModel','{']
    for name,array in zip(names,params):
        lines+=['    internal static readonly double[] '+name+' =','    [']
        flat=array.ravel()
        for start in range(0,len(flat),8): lines.append('        '+', '.join(format(float(v),'.10g') for v in flat[start:start+8])+',')
        lines.append('    ];')
    lines+=['}','']
    destination=Path(__file__).resolve().parent.parent/'src/BabbleFrameBridge.App/KittyModel.cs'
    destination.write_text('\n'.join(lines),encoding='utf-8')
    print(f'Generated {count} parameters / {count*8:,} numeric bytes; behavior {agreement:.2%}, attention {gaze_agreement:.2%}.',flush=True)

if __name__=='__main__': main()
