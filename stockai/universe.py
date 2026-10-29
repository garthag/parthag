"""Sector-wise universe of liquid NSE stocks scanned by the suggestion box."""

SECTORS = {
    "Banks": ["HDFCBANK", "ICICIBANK", "SBIN", "KOTAKBANK", "AXISBANK", "BANKBARODA"],
    "NBFC & insurance": ["BAJFINANCE", "SHRIRAMFIN", "CHOLAFIN", "BAJAJFINSV", "HDFCLIFE", "SBILIFE", "BSE"],
    "IT services": ["TCS", "INFY", "HCLTECH", "WIPRO", "TECHM", "LTIM", "PERSISTENT"],
    "Pharma & healthcare": ["SUNPHARMA", "DRREDDY", "CIPLA", "LUPIN", "DIVISLAB", "APOLLOHOSP", "MAXHEALTH"],
    "Automobiles": ["MARUTI", "M&M", "BAJAJ-AUTO", "TVSMOTOR", "EICHERMOT", "HEROMOTOCO"],
    "Oil & gas": ["RELIANCE", "ONGC", "BPCL", "IOC", "HINDPETRO", "GAIL", "OIL"],
    "FMCG & retail": ["HINDUNILVR", "ITC", "NESTLEIND", "BRITANNIA", "TATACONSUM", "TITAN", "TRENT", "ETERNAL"],
    "Metals & mining": ["TATASTEEL", "JSWSTEEL", "HINDALCO", "VEDL", "HINDZINC", "NMDC", "COALINDIA"],
    "Power": ["NTPC", "POWERGRID", "TATAPOWER", "ADANIGREEN", "NHPC", "JSWENERGY"],
    "Capital goods & infra": ["LT", "SIEMENS", "ABB", "BHEL", "CUMMINSIND", "POLYCAB"],
    "Defence": ["HAL", "BEL", "BDL", "MAZDOCK", "COCHINSHIP", "SOLARINDS"],
    "Cement, paints & building": ["ULTRACEMCO", "AMBUJACEM", "SHREECEM", "ASIANPAINT", "BERGEPAINT", "PIDILITIND"],
    "Telecom, aviation & tyres": ["BHARTIARTL", "INDIGO", "APOLLOTYRE", "MRF"],
}


def all_symbols() -> list[str]:
    return [s for syms in SECTORS.values() for s in syms]


def sector_of(code: str) -> str:
    for sector, syms in SECTORS.items():
        if code in syms:
            return sector
    return "Other"


NAMES = {
    "BANKBARODA": "Bank of Baroda", "SHRIRAMFIN": "Shriram Finance", "CHOLAFIN": "Cholamandalam Finance",
    "BSE": "BSE Ltd", "LTIM": "LTIMindtree", "PERSISTENT": "Persistent Systems", "LUPIN": "Lupin",
    "DIVISLAB": "Divi's Laboratories", "APOLLOHOSP": "Apollo Hospitals", "MAXHEALTH": "Max Healthcare",
    "BAJAJ-AUTO": "Bajaj Auto", "TVSMOTOR": "TVS Motor", "EICHERMOT": "Eicher Motors", "HEROMOTOCO": "Hero MotoCorp",
    "BPCL": "Bharat Petroleum", "IOC": "Indian Oil", "HINDPETRO": "Hindustan Petroleum", "GAIL": "GAIL (India)",
    "OIL": "Oil India", "BRITANNIA": "Britannia", "TATACONSUM": "Tata Consumer", "TRENT": "Trent",
    "ETERNAL": "Eternal (Zomato)", "HINDALCO": "Hindalco", "VEDL": "Vedanta", "HINDZINC": "Hindustan Zinc",
    "NMDC": "NMDC", "TATAPOWER": "Tata Power", "ADANIGREEN": "Adani Green", "NHPC": "NHPC",
    "JSWENERGY": "JSW Energy", "SIEMENS": "Siemens", "ABB": "ABB India", "BHEL": "BHEL",
    "CUMMINSIND": "Cummins India", "POLYCAB": "Polycab", "BDL": "Bharat Dynamics", "MAZDOCK": "Mazagon Dock",
    "COCHINSHIP": "Cochin Shipyard", "SOLARINDS": "Solar Industries", "AMBUJACEM": "Ambuja Cements",
    "SHREECEM": "Shree Cement", "BERGEPAINT": "Berger Paints", "PIDILITIND": "Pidilite", "INDIGO": "InterGlobe (IndiGo)",
    "APOLLOTYRE": "Apollo Tyres", "MRF": "MRF", "TECHM": "Tech Mahindra",
}
