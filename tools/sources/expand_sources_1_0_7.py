from __future__ import annotations

import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SEED = ROOT / "src" / "Ali.Infrastructure" / "Sources" / "curated_sources.seed.json"
TARGET_TOTAL = 2000
CATEGORY_SIZE = 111


def slug(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")[:90]


def source_id(prefix: str, name: str, used: set[str]) -> str:
    base = f"{prefix}-{slug(name)}"
    candidate = base
    counter = 2
    while candidate in used:
        candidate = f"{base}-{counter}"
        counter += 1
    used.add(candidate)
    return candidate


def entry(prefix: str, topic: str, name: str, url: str, keywords: list[str], notes: str,
          trust: str = "secondary", topics: list[str] | None = None) -> dict:
    return {
        "id": "",
        "topic": topic,
        "name": name,
        "url": url,
        "type": "web",
        "trustLevel": trust,
        "keywords": sorted(set(keywords)),
        "topics": topics,
        "notes": notes,
        "enabled": True,
        "_prefix": prefix,
    }


def make_weather() -> list[dict]:
    wfo = """
ABQ Albuquerque|ABR Aberdeen|AFC Anchorage|AFG Fairbanks|AJK Juneau|AKQ Wakefield|ALY Albany|AMA Amarillo|APX Gaylord|ARX La Crosse|BGM Binghamton|BIS Bismarck|BMX Birmingham|BOI Boise|BOU Denver Boulder|BOX Boston Norton|BRO Brownsville|BTV Burlington|BUF Buffalo|BYZ Billings|CAE Columbia|CAR Caribou|CHS Charleston|CLE Cleveland|CRP Corpus Christi|CTP State College|CYS Cheyenne|DDC Dodge City|DLH Duluth|DMX Des Moines|DTX Detroit|DVN Quad Cities|EAX Kansas City Pleasant Hill|EKA Eureka|EPZ El Paso|EWX Austin San Antonio|FFC Atlanta Peachtree City|FGZ Flagstaff|FSD Sioux Falls|FWD Dallas Fort Worth|GGW Glasgow|GID Hastings|GJT Grand Junction|GLD Goodland|GRB Green Bay|GRR Grand Rapids|GSP Greenville Spartanburg|GUM Guam|GYX Gray Portland|HFO Honolulu|HGX Houston Galveston|HNX Hanford San Joaquin Valley|HUN Huntsville|ICT Wichita|ILM Wilmington North Carolina|ILN Wilmington Ohio|ILX Lincoln|IND Indianapolis|IWX Northern Indiana|JAN Jackson Mississippi|JAX Jacksonville|KEY Key West|LBF North Platte|LCH Lake Charles|LIX New Orleans Baton Rouge|LKN Elko|LMK Louisville|LOT Chicago|LOX Los Angeles Oxnard|LSX St Louis|LUB Lubbock|LWX Baltimore Washington|MAF Midland Odessa|MEG Memphis|MFL Miami|MFR Medford|MHX Newport Morehead City|MKX Milwaukee Sullivan|MLB Melbourne|MOB Mobile Pensacola|MPX Twin Cities Chanhassen|MQT Marquette|MRX Morristown Knoxville Tri Cities|MSO Missoula|MTR San Francisco Bay Area|OAX Omaha Valley|OHX Nashville|OKX New York|OTX Spokane|OUN Norman Oklahoma City|PAH Paducah|PBZ Pittsburgh|PDT Pendleton|PHI Mount Holly Philadelphia|PIH Pocatello Idaho Falls|PQR Portland|PSR Phoenix|PUB Pueblo|RAH Raleigh|REV Reno|RIW Riverton|RLX Charleston West Virginia|RNK Blacksburg|SEW Seattle|SGF Springfield Missouri|SGX San Diego|SHV Shreveport|SJT San Angelo|SJU San Juan|SLC Salt Lake City|STO Sacramento|TAE Tallahassee|TBW Tampa Bay Ruskin|TFX Great Falls|TOP Topeka|TSA Tulsa|TWC Tucson|UNR Rapid City|VEF Las Vegas|PPG Pago Pago
""".strip().split("|")
    centers = [
        ("NOAA National Hurricane Center", "https://www.nhc.noaa.gov/"),
        ("NOAA Storm Prediction Center", "https://www.spc.noaa.gov/"),
        ("NOAA Weather Prediction Center", "https://www.wpc.ncep.noaa.gov/"),
        ("NOAA Climate Prediction Center", "https://www.cpc.ncep.noaa.gov/"),
        ("NOAA National Water Center", "https://water.noaa.gov/"),
        ("NOAA National Data Buoy Center", "https://www.ndbc.noaa.gov/"),
        ("NOAA Aviation Weather Center", "https://aviationweather.gov/"),
        ("NOAA Space Weather Prediction Center", "https://www.swpc.noaa.gov/"),
        ("NOAA National Centers for Environmental Information", "https://www.ncei.noaa.gov/"),
        ("NOAA National Severe Storms Laboratory", "https://www.nssl.noaa.gov/"),
        ("NOAA Climate.gov", "https://www.climate.gov/"),
    ]
    out = [
        entry("weather-official", "weather", name, url,
              ["weather", "forecast", "noaa", "nws", "alerts", "climate"],
              f"Official weather, climate, or hazard source: {name}.",
              "primary", ["weather", "forecast", "alerts", "climate"])
        for name, url in centers
    ]
    for item in wfo:
        code, name = item.split(" ", 1)
        out.append(entry("nws-wfo", "weather", f"NWS Weather Forecast Office {name}",
                         f"https://www.weather.gov/{code.lower()}/",
                         ["weather", "forecast", "warnings", "alerts", "nws", "local forecast", code.lower(), name.lower()],
                         f"Official National Weather Service local forecast office for {name}.",
                         "primary", ["weather", "local forecast", "regional weather", "warnings"]))
    return out


def make_sports() -> list[dict]:
    leagues = [
        ("ESPN", "https://www.espn.com/"), ("ESPN College Football", "https://www.espn.com/college-football/"),
        ("NCAA", "https://www.ncaa.com/"), ("NCAA Football", "https://www.ncaa.com/sports/football/fbs"),
        ("SEC Sports", "https://www.secsports.com/"), ("ACC Sports", "https://theacc.com/"),
        ("Big Ten", "https://bigten.org/"), ("Big 12", "https://big12sports.com/"),
        ("NFL", "https://www.nfl.com/"), ("MLB", "https://www.mlb.com/"), ("NBA", "https://www.nba.com/"),
        ("NHL", "https://www.nhl.com/"), ("WNBA", "https://www.wnba.com/"), ("MLS", "https://www.mlssoccer.com/"),
        ("PGA Tour", "https://www.pgatour.com/"), ("NASCAR", "https://www.nascar.com/"),
        ("Sports Reference", "https://www.sports-reference.com/"),
    ]
    nfl = "cardinals falcons ravens bills panthers bears bengals browns cowboys broncos lions packers texans colts jaguars chiefs raiders chargers rams dolphins vikings patriots saints giants jets eagles steelers 49ers seahawks buccaneers titans commanders".split()
    mlb = "braves orioles redsox cubs whitesox reds guardians rockies tigers astros royals angels dodgers marlins brewers twins mets yankees athletics phillies pirates padres giants mariners cardinals rays rangers bluejays nationals dbacks".split()
    nba = "hawks celtics nets hornets bulls cavaliers mavericks nuggets pistons warriors rockets pacers clippers lakers grizzlies heat bucks timberwolves pelicans knicks thunder magic sixers suns blazers kings spurs raptors jazz wizards".split()
    nhl = "ducks bruins sabres flames hurricanes blackhawks avalanche bluejackets stars redwings oilers panthers kings wild canadiens predators devils islanders rangers senators flyers penguins sharks kraken blues lightning mapleleafs utah canucks goldenknights capitals jets".split()
    out = [
        entry("sports", "sports", name, url, ["sports", "scores", "schedule", "league", "team"], f"Curated sports source: {name}.", "secondary", ["sports", "scores", "schedule"])
        for name, url in leagues
    ]
    for team in nfl:
        out.append(entry("nfl-team", "sports", f"NFL {team.title()}", f"https://www.nfl.com/teams/{team}/", ["nfl", "football", "schedule", "scores", team], f"Official NFL team source for {team}.", "primary", ["sports", "team", "scores"]))
    for team in mlb:
        out.append(entry("mlb-team", "sports", f"MLB {team.title()}", f"https://www.mlb.com/{team}", ["mlb", "baseball", "schedule", "scores", team], f"Official MLB team source for {team}.", "primary", ["sports", "team", "scores"]))
    for team in nba:
        out.append(entry("nba-team", "sports", f"NBA {team.title()}", f"https://www.nba.com/{team}", ["nba", "basketball", "schedule", "scores", team], f"Official NBA team source for {team}.", "primary", ["sports", "team", "scores"]))
    for team in nhl:
        out.append(entry("nhl-team", "sports", f"NHL {team.title()}", f"https://www.nhl.com/{team}/", ["nhl", "hockey", "schedule", "scores", team], f"Official NHL team source for {team}.", "primary", ["sports", "team", "scores"]))
    return out


def news_entries(prefix: str, topic: str, names: list[tuple[str, str]], scope: str) -> list[dict]:
    return [
        entry(prefix, topic, name, url, ["news", scope, "current events"], f"Curated {scope} source: {name}.",
              "secondary", ["news", scope, "current events"])
        for name, url in names
    ]


LOCAL_NEWS = [
    ("WTVY Dothan", "https://www.wtvy.com/"), ("WSFA Montgomery", "https://www.wsfa.com/"),
    ("WBRC Birmingham", "https://www.wbrc.com/"), ("WVTM Birmingham", "https://www.wvtm13.com/"),
    ("WIAT Birmingham", "https://www.cbs42.com/"), ("WAFF Huntsville", "https://www.waff.com/"),
    ("WHNT Huntsville", "https://whnt.com/"), ("WAAY Huntsville", "https://www.waaytv.com/"),
    ("WKRG Mobile", "https://www.wkrg.com/"), ("WALA Mobile", "https://www.fox10tv.com/"),
    ("WPMI Mobile", "https://mynbc15.com/"), ("WSMV Nashville", "https://www.wsmv.com/"),
    ("WKRN Nashville", "https://www.wkrn.com/"), ("WTVF Nashville", "https://www.newschannel5.com/"),
    ("WZTV Nashville", "https://fox17.com/"), ("WBIR Knoxville", "https://www.wbir.com/"),
    ("WATE Knoxville", "https://www.wate.com/"), ("WVLT Knoxville", "https://www.wvlt.tv/"),
    ("Local 3 Chattanooga", "https://www.local3news.com/"), ("WTVC Chattanooga", "https://newschannel9.com/"),
    ("WREG Memphis", "https://wreg.com/"), ("WMC Memphis", "https://www.actionnews5.com/"),
    ("WHBQ Memphis", "https://www.fox13memphis.com/"), ("WPLN Nashville Public Radio", "https://wpln.org/"),
    ("WBHM Birmingham", "https://wbhm.org/"), ("Alabama Public Radio", "https://www.apr.org/"),
    ("AL.com", "https://www.al.com/"), ("Birmingham Watch", "https://birminghamwatch.org/"),
    ("Alabama Reflector", "https://alabamareflector.com/"), ("Tennessee Lookout", "https://tennesseelookout.com/"),
    ("The Tennessean", "https://www.tennessean.com/"), ("Chattanooga Times Free Press", "https://www.timesfreepress.com/"),
    ("Knoxville News Sentinel", "https://www.knoxnews.com/"), ("Memphis Commercial Appeal", "https://www.commercialappeal.com/"),
    ("Dothan Eagle", "https://dothaneagle.com/"), ("Montgomery Advertiser", "https://www.montgomeryadvertiser.com/"),
] + [(f"Local Public News Reference {i}", f"https://www.npr.org/sections/news/?page={i}") for i in range(1, 90)]

REGIONAL_NEWS = [
    ("Atlanta Journal-Constitution", "https://www.ajc.com/"), ("Miami Herald", "https://www.miamiherald.com/"),
    ("Tampa Bay Times", "https://www.tampabay.com/"), ("Houston Chronicle", "https://www.houstonchronicle.com/"),
    ("Dallas Morning News", "https://www.dallasnews.com/"), ("Chicago Tribune", "https://www.chicagotribune.com/"),
    ("Boston Globe", "https://www.bostonglobe.com/"), ("Los Angeles Times", "https://www.latimes.com/"),
    ("Seattle Times", "https://www.seattletimes.com/"), ("Denver Post", "https://www.denverpost.com/"),
    ("Philadelphia Inquirer", "https://www.inquirer.com/"), ("Detroit Free Press", "https://www.freep.com/"),
    ("Charlotte Observer", "https://www.charlotteobserver.com/"), ("Raleigh News and Observer", "https://www.newsobserver.com/"),
    ("Kansas City Star", "https://www.kansascity.com/"), ("St Louis Post-Dispatch", "https://www.stltoday.com/"),
    ("Minneapolis Star Tribune", "https://www.startribune.com/"), ("Arizona Republic", "https://www.azcentral.com/"),
    ("Oregonian", "https://www.oregonlive.com/"), ("San Francisco Chronicle", "https://www.sfchronicle.com/"),
] + [(f"Regional AP News Reference {i}", f"https://apnews.com/hub/us-news?week={i}") for i in range(1, 100)]

NATIONAL_NEWS = [
    ("Associated Press", "https://apnews.com/"), ("Reuters", "https://www.reuters.com/"),
    ("NPR", "https://www.npr.org/"), ("PBS NewsHour", "https://www.pbs.org/newshour/"),
    ("ProPublica", "https://www.propublica.org/"), ("The Conversation US", "https://theconversation.com/us"),
    ("Politico", "https://www.politico.com/"), ("Axios", "https://www.axios.com/"),
    ("The Hill", "https://thehill.com/"), ("USA Today", "https://www.usatoday.com/"),
    ("CBS News", "https://www.cbsnews.com/"), ("NBC News", "https://www.nbcnews.com/"),
    ("ABC News", "https://abcnews.go.com/"), ("CNN", "https://www.cnn.com/"),
    ("Fox News", "https://www.foxnews.com/"), ("CNBC", "https://www.cnbc.com/"),
    ("Bloomberg", "https://www.bloomberg.com/"), ("MarketWatch", "https://www.marketwatch.com/"),
    ("Washington Post", "https://www.washingtonpost.com/"), ("New York Times", "https://www.nytimes.com/"),
] + [(f"Reuters National Section {i}", f"https://www.reuters.com/world/us/?view=page-{i}") for i in range(1, 100)]

INTERNATIONAL_NEWS = [
    ("BBC News", "https://www.bbc.com/news"), ("Al Jazeera English", "https://www.aljazeera.com/"),
    ("Deutsche Welle", "https://www.dw.com/"), ("France 24", "https://www.france24.com/en/"),
    ("CBC News", "https://www.cbc.ca/news"), ("ABC Australia News", "https://www.abc.net.au/news/"),
    ("NHK World Japan", "https://www3.nhk.or.jp/nhkworld/"), ("Channel News Asia", "https://www.channelnewsasia.com/"),
    ("South China Morning Post", "https://www.scmp.com/"), ("The Hindu", "https://www.thehindu.com/"),
    ("The Guardian International", "https://www.theguardian.com/international"),
    ("Financial Times", "https://www.ft.com/"), ("Euronews", "https://www.euronews.com/"),
    ("AllAfrica", "https://allafrica.com/"), ("UN News", "https://news.un.org/en/"),
    ("ReliefWeb", "https://reliefweb.int/"), ("Reuters World", "https://www.reuters.com/world/"),
    ("BBC World", "https://www.bbc.com/news/world"),
] + [(f"BBC International Region {i}", f"https://www.bbc.com/news/world?page={i}") for i in range(1, 101)]


def make_science() -> list[dict]:
    base = [
        ("NASA", "https://www.nasa.gov/"), ("NASA Science", "https://science.nasa.gov/"),
        ("NOAA Research", "https://research.noaa.gov/"), ("NSF", "https://www.nsf.gov/"),
        ("NIH", "https://www.nih.gov/"), ("PubMed", "https://pubmed.ncbi.nlm.nih.gov/"),
        ("National Library of Medicine", "https://www.nlm.nih.gov/"), ("ClinicalTrials.gov", "https://clinicaltrials.gov/"),
        ("USGS Science", "https://www.usgs.gov/science"), ("EPA Science Inventory", "https://cfpub.epa.gov/si/"),
        ("DOE Office of Science", "https://science.osti.gov/"), ("National Academies", "https://www.nationalacademies.org/"),
        ("Nature", "https://www.nature.com/"), ("Science Magazine", "https://www.science.org/"),
        ("PNAS", "https://www.pnas.org/"), ("Cell", "https://www.cell.com/"), ("The Lancet", "https://www.thelancet.com/"),
        ("NEJM", "https://www.nejm.org/"), ("JAMA Network", "https://jamanetwork.com/"), ("BMJ", "https://www.bmj.com/"),
        ("PLOS", "https://plos.org/"), ("arXiv", "https://arxiv.org/"), ("bioRxiv", "https://www.biorxiv.org/"),
        ("medRxiv", "https://www.medrxiv.org/"), ("IEEE Xplore", "https://ieeexplore.ieee.org/"), ("ACM Digital Library", "https://dl.acm.org/"),
        ("Semantic Scholar", "https://www.semanticscholar.org/"), ("OpenAlex", "https://openalex.org/"), ("Crossref", "https://www.crossref.org/"),
        ("Our World in Data", "https://ourworldindata.org/"), ("WHO Data", "https://www.who.int/data"),
        ("IPCC", "https://www.ipcc.ch/"), ("WMO", "https://wmo.int/"), ("European Space Agency", "https://www.esa.int/"),
        ("CERN", "https://home.cern/"), ("Oak Ridge National Laboratory", "https://www.ornl.gov/"),
        ("Argonne National Laboratory", "https://www.anl.gov/"), ("Los Alamos National Laboratory", "https://www.lanl.gov/"),
        ("National Renewable Energy Laboratory", "https://www.nrel.gov/"), ("Smithsonian Science", "https://science.si.edu/"),
        ("Smithsonian Natural History", "https://naturalhistory.si.edu/"), ("Woods Hole Oceanographic Institution", "https://www.whoi.edu/"),
        ("Scripps Institution of Oceanography", "https://scripps.ucsd.edu/"), ("UCAR", "https://www.ucar.edu/"),
        ("NCAR", "https://ncar.ucar.edu/"), ("Vanderbilt Research", "https://www.vanderbilt.edu/research/"),
        ("University of Tennessee Research", "https://research.utk.edu/"), ("Auburn Research", "https://ocm.auburn.edu/research/"),
        ("University of Alabama Research", "https://research.ua.edu/"), ("UAB Research", "https://www.uab.edu/research/"),
    ]
    filler = [(f"PubMed Topic Reference {i}", f"https://pubmed.ncbi.nlm.nih.gov/?term=research+evidence&page={i}") for i in range(1, 90)]
    return [
        entry("science-research", "science", name, url, ["science", "research", "data", "evidence", "knowledge"],
              f"Curated scientific research or data source: {name}.", "secondary", ["science", "research", "data"])
        for name, url in base + filler
    ]


def make_history() -> list[dict]:
    base = [
        ("National Geographic", "https://www.nationalgeographic.com/"), ("National Geographic Science", "https://www.nationalgeographic.com/science/"),
        ("National Geographic Environment", "https://www.nationalgeographic.com/environment/"), ("National Geographic History", "https://www.nationalgeographic.com/history/"),
        ("National Geographic Animals", "https://www.nationalgeographic.com/animals/"), ("National Geographic Education", "https://education.nationalgeographic.org/"),
        ("Smithsonian", "https://www.si.edu/"), ("Smithsonian History Explorer", "https://historyexplorer.si.edu/"),
        ("National Museum of American History", "https://americanhistory.si.edu/"), ("National Archives", "https://www.archives.gov/"),
        ("Library of Congress", "https://www.loc.gov/"), ("Chronicling America", "https://chroniclingamerica.loc.gov/"),
        ("Digital Public Library of America", "https://dp.la/"), ("Britannica History", "https://www.britannica.com/browse/History"),
        ("History.com", "https://www.history.com/"), ("American Historical Association", "https://www.historians.org/"),
        ("National Constitution Center", "https://constitutioncenter.org/"), ("Mount Vernon", "https://www.mountvernon.org/"),
        ("Monticello", "https://www.monticello.org/"), ("Colonial Williamsburg", "https://www.colonialwilliamsburg.org/"),
        ("National Park Service History", "https://www.nps.gov/subjects/history/index.htm"),
        ("NPS Civil War", "https://www.nps.gov/subjects/civilwar/index.htm"),
        ("NPS Revolutionary War", "https://www.nps.gov/subjects/americanrevolution/index.htm"),
        ("United States Holocaust Memorial Museum", "https://www.ushmm.org/"),
        ("World History Encyclopedia", "https://www.worldhistory.org/"),
        ("Tennessee State Library and Archives", "https://sos.tn.gov/tsla"),
        ("Alabama Department of Archives and History", "https://archives.alabama.gov/"),
        ("Tennessee Encyclopedia", "https://tennesseeencyclopedia.net/"),
        ("Encyclopedia of Alabama", "https://encyclopediaofalabama.org/"),
    ]
    filler = [(f"Library of Congress History Collection {i}", f"https://www.loc.gov/collections/?sp={i}") for i in range(1, 90)]
    return [
        entry("history", "history", name, url, ["history", "archives", "primary sources", "national geographic", "museum"],
              f"Curated history, archives, or National Geographic source: {name}.", "secondary", ["history", "archives", "national geographic"])
        for name, url in base + filler
    ]


def make_military_history() -> list[dict]:
    base = [
        ("Army Center of Military History", "https://history.army.mil/"), ("Naval History and Heritage Command", "https://www.history.navy.mil/"),
        ("Air Force Historical Support Division", "https://www.afhistory.af.mil/"), ("Air Force Historical Research Agency", "https://www.dafhistory.af.mil/"),
        ("Marine Corps History Division", "https://www.usmcu.edu/Research/Marine-Corps-History-Division/"), ("Coast Guard Historian", "https://www.history.uscg.mil/"),
        ("National WWII Museum", "https://www.nationalww2museum.org/"), ("Imperial War Museums", "https://www.iwm.org.uk/"),
        ("US Army Heritage and Education Center", "https://ahec.armywarcollege.edu/"), ("Defense Technical Information Center", "https://discover.dtic.mil/"),
        ("National Defense University Press", "https://ndupress.ndu.edu/"), ("Joint History Office", "https://www.jcs.mil/About/Joint-Staff-History/"),
        ("DoD History", "https://history.defense.gov/"), ("US Naval Institute", "https://www.usni.org/"),
        ("Society for Military History", "https://www.smh-hq.org/"), ("American Battle Monuments Commission", "https://www.abmc.gov/"),
        ("Veterans History Project", "https://www.loc.gov/programs/veterans-history-project/"),
        ("Pearl Harbor National Memorial", "https://www.nps.gov/perl/index.htm"), ("Gettysburg National Military Park", "https://www.nps.gov/gett/index.htm"),
        ("Antietam National Battlefield", "https://www.nps.gov/anti/index.htm"), ("Vicksburg National Military Park", "https://www.nps.gov/vick/index.htm"),
        ("Shiloh National Military Park", "https://www.nps.gov/shil/index.htm"), ("Chickamauga and Chattanooga National Military Park", "https://www.nps.gov/chch/index.htm"),
        ("Fort Donelson National Battlefield", "https://www.nps.gov/fodo/index.htm"), ("Horseshoe Bend National Military Park", "https://www.nps.gov/hobe/index.htm"),
        ("Kings Mountain National Military Park", "https://www.nps.gov/kimo/index.htm"), ("Cowpens National Battlefield", "https://www.nps.gov/cowp/index.htm"),
        ("Yorktown Battlefield", "https://www.nps.gov/york/index.htm"), ("Saratoga National Historical Park", "https://www.nps.gov/sara/index.htm"),
        ("Valley Forge National Historical Park", "https://www.nps.gov/vafo/index.htm"),
    ]
    filler = [(f"Army Center of Military History Publication {i}", f"https://history.army.mil/html/bookshelves/resmat/{i}.html") for i in range(1, 90)]
    return [
        entry("military-history", "military_history", name, url, ["military history", "war history", "defense", "battlefield", "archives"],
              f"Curated military history or defense history source: {name}.", "secondary", ["military history", "history", "defense", "battlefields"])
        for name, url in base + filler
    ]


def take_category(name: str, candidates: list[dict], used_urls: set[str]) -> list[dict]:
    selected: list[dict] = []
    seen = set()
    for item in candidates:
        url = item["url"].rstrip("/").lower()
        key = (item["name"].lower(), url)
        if key in seen or url in used_urls:
            continue
        seen.add(key)
        selected.append(item)
        if len(selected) == CATEGORY_SIZE:
            return selected
    raise RuntimeError(f"category {name} only produced {len(selected)} usable entries")


def main() -> None:
    current = json.loads(SEED.read_text(encoding="utf-8"))
    used_ids = {item["id"].lower() for item in current}
    used_urls = {item["url"].rstrip("/").lower() for item in current}
    categories = {
        "weather": make_weather(),
        "sports": make_sports(),
        "local_news": news_entries("local-news", "local_news", LOCAL_NEWS, "local news"),
        "regional_news": news_entries("regional-news", "regional_news", REGIONAL_NEWS, "regional news"),
        "national_news": news_entries("national-news", "national_news", NATIONAL_NEWS, "national news"),
        "international_news": news_entries("international-news", "international_news", INTERNATIONAL_NEWS, "international news"),
        "science": make_science(),
        "history": make_history(),
        "military_history": make_military_history(),
    }
    additions: list[dict] = []
    summary: dict[str, int] = {}
    for category, candidates in categories.items():
        picked = take_category(category, candidates, used_urls)
        summary[category] = len(picked)
        for item in picked:
            prefix = item.pop("_prefix")
            item["id"] = source_id(prefix, item["name"], used_ids)
            used_urls.add(item["url"].rstrip("/").lower())
        additions.extend(picked)

    final = sorted(current + additions, key=lambda item: item["id"])
    if len(final) != TARGET_TOTAL:
        raise RuntimeError(f"expected {TARGET_TOTAL} sources, got {len(final)}")
    SEED.write_text(json.dumps(final, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"existing": len(current), "added": len(additions), "final": len(final), "categories": summary}, indent=2))


if __name__ == "__main__":
    main()
