"""Tags for a file, read from its embedding.

A vocabulary of short describing words is embedded once (cached on disk). For one file, each tag
is scored against it and compared with how that tag scores across the whole library, so a tag
is only given when it fits this file clearly better than it fits files in general (generic words
like "photo" do not end up on everything)."""
import hashlib
import threading

import numpy as np

from . import embedder, indexer
from .db import DATA_DIR

VOCAB = """
man, woman, boy, girl, child, baby, old man, old woman, couple, family, crowd, group of people, portrait, close-up face,
smiling, laughing, serious face, sad, angry, eyes, hands, beard, long hair, red hair, blonde hair, bald, glasses,
sunglasses, tattoo, makeup, shirtless, muscular, athlete, dancer, model, actor, singer, musician, chef, doctor, soldier,
police, detective, businessman, student, worker, farmer, astronaut, robot, cyborg, superhero, warrior, knight, ninja,
suit, tuxedo, dress, saree, kurta, sherwani, traditional clothing, jacket, leather jacket, trench coat, hoodie, t-shirt,
jeans, uniform, armor, hat, cap, helmet, scarf, jewelry, necklace, watch, ring, earrings, handbag, backpack,
sneakers, shoes, boots, heels, perfume, cosmetics, lipstick, bottle, can, cup, coffee, tea, wine, cocktail, beer,
juice, water, food, burger, pizza, cake, dessert, fruit, vegetables, rice, noodles, bread, chocolate, ice cream,
car, sports car, truck, bus, motorcycle, bicycle, train, airplane, helicopter, boat, ship, rocket, drone,
phone, smartphone, laptop, computer, monitor, keyboard, camera, headphones, speaker, microphone, tv, tablet, smartwatch,
furniture, sofa, chair, table, bed, lamp, plant, flowers, rose, tree, forest, woods, jungle, grass field, garden,
mountain, hill, valley, desert, sand dunes, beach, ocean, sea, waves, lake, river, waterfall, island, cliff, cave,
snow, ice, rain, fog, mist, clouds, storm, lightning, rainbow, sunrise, sunset, golden hour, night, moon, stars,
galaxy, space, planet, sky, city, skyline, street, alley, road, highway, bridge, building, skyscraper, house, village,
temple, mosque, church, castle, palace, ruins, market, shop, restaurant, cafe, bar, kitchen, bedroom, living room,
office, studio, warehouse, factory, gym, stadium, stage, concert, festival, wedding, party, red carpet, classroom,
hospital, library, museum, airport, train station, rooftop, balcony, window, door, stairs, corridor, interior, exterior,
dog, cat, horse, cow, bird, eagle, owl, fish, shark, whale, lion, tiger, elephant, monkey, bear, wolf, deer, snake,
butterfly, insect, dragon, creature, monster, ghost, zombie, alien,
cricket, football, basketball, tennis, boxing, running, swimming, cycling, yoga, dancing, fighting, walking,
driving, flying, jumping, sitting, standing, sleeping, eating, drinking, cooking, reading, writing, talking,
text, typography, logo, poster, banner, advertisement, product shot, packaging, label, infographic, chart, graph,
map, diagram, screenshot, user interface, website, app screen, presentation slide, document, handwriting, sketch,
drawing, painting, watercolor, oil painting, illustration, cartoon, anime, comic, pixel art, 3d render, cgi,
low poly, clay, paper cut, collage, pattern, texture, abstract, geometric, gradient, minimal, maximal,
cinematic, film still, dramatic lighting, backlight, silhouette, spotlight, neon, glow, lens flare, bokeh,
motion blur, long exposure, slow motion, aerial view, drone shot, wide shot, close-up, macro, low angle, high angle,
top-down view, symmetry, black and white, monochrome, vintage, retro, film grain, polaroid, futuristic, sci-fi,
cyberpunk, fantasy, horror, dark, moody, bright, colorful, pastel, warm tones, cool tones, golden, silver, metallic,
luxury, elegant, playful, cute, scary, calm, peaceful, energetic, romantic, mysterious, epic,
fire, flames, smoke, explosion, sparks, particles, water splash, liquid, bubbles, glass, reflection, mirror, shadow,
green screen, white background, black background, studio background, transparent background,
music, song, vocals, speech, voiceover, interview, podcast, narration, piano, guitar, drums, orchestra, electronic beat,
hip hop, ambient, sound effect, applause, laughter, crowd noise, nature sounds, birdsong
"""
TAGS = list(dict.fromkeys(t.strip() for t in VOCAB.replace("\n", " ").split(",") if t.strip()))

_lock = threading.Lock()
_st = {"vecs": None, "key": None, "mu": None, "sd": None}


def _vecs():
    with _lock:
        if _st["vecs"] is None:
            h = hashlib.sha1(("\n".join(TAGS) + "|" + str(embedder.MODEL_ID)).encode()).hexdigest()[:12]
            f = DATA_DIR / f"tags_{h}.npy"
            if f.exists():
                _st["vecs"] = np.load(f)
            else:
                v = embedder.get_model().encode(TAGS, prompt_name="SearchQuery", normalize_embeddings=True, batch_size=64)
                _st["vecs"] = np.asarray(v, dtype=np.float32)
                np.save(f, _st["vecs"])
        return _st["vecs"]


def _stats(ids, mat):
    key = (len(ids), int(ids[-1]) if len(ids) else 0)
    if _st["key"] != key:
        s = _vecs() @ mat.T                       # tags × files
        _st.update(key=key, mu=s.mean(axis=1), sd=s.std(axis=1) + 1e-6)
    return _st["mu"], _st["sd"]


def warm():
    ids, mat = indexer.matrix()
    if len(ids):
        _stats(ids, mat)


def for_vector(v, n=10, min_z=1.5, spread=0.06):
    """Best-fitting tags first, keeping only ones that suit this file more than files in general,
    and that fit nearly as well as the best one."""
    ids, mat = indexer.matrix()
    if len(ids) < 8:
        return []
    mu, sd = _stats(ids, mat)
    raw = _vecs() @ v
    z = (raw - mu) / sd
    picks = [i for i in np.argsort(-raw) if z[i] >= min_z][:n]
    return [TAGS[i] for i in picks if raw[i] >= raw[picks[0]] - spread] if picks else []
