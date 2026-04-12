Introduction
The SimBrief API was created for developers who wish to include SimBrief flight planning features on their website.

When a flight plan is generated using the API, it returns an XML data file containing nearly every internal SimBrief variable which was used in the process. This provides the utmost in flexibility; developers seeking a quick and easy solution might choose to simply display the OFP text and/or PDF file on their VA’s dispatch page, while others may want to create an entire custom dispatch system along with a custom OFP layout. Because of the wide range of variables this API returns, there are many possibilities!

How it Works
First and foremost, this API does not bypass the SimBrief login system. This means that should you develop dispatch tools using the API, any pilots who wish to use them will require a SimBrief (or Navigraph) account. This is necessary as SimBrief must be able to track which AIRAC cycle to deliver to the user currently using your system.

The way you design your system is largely up to you, however when one of your users finally requests a flight plan (by pressing your system’s “Generate” button, for example), a small popup window is created to house the SimBrief background process. If your pilot is not already logged into the SimBrief website, this popup window will ask them to enter their username and password before continuing. The popup will then display a progress bar indicating the flight plan’s progress.

Once done, the popup window will automatically close and the pilot will be sent back to your website, where they will be able to view their flight plan however you choose to display it.

Using the API
Getting started is relatively simple, as all the required Javascript and PHP functions are provided for you. The basic guidelines are as follows:

On the page which calls the API, you will need to include an html <form> containing the dispatch options you would like to use. The <input> names for each option are the same as those listed in the VA Integration thread. I have pasted that table below for ease of reference. Inputs not named properly will be ignored when generating the flight plan.

At the very least, you must include the following inputs: ‘orig’, ‘dest’, and ‘type’. Any other omitted inputs will be replaced with default values. If the ‘route’ input is omitted, it will be replaced with the recommended route from SimBrief database.

You must include <script type=“text/javascript” src=“simbrief.apiv1.js”></script> in your html tag, as it contains the necessary functions for the API to operate.

The <form> containing your Dispatch Options must have at least the following property:
<form id=“sbapiform”>.

Your form’s submit button must have at least the following property:
<input type=“button” onclick=“simbriefsubmit(‘referral_page’);”> , where “referral_page” is the link to the page you want the API to redirect to when the flight plan is ready. This is normally the output page on your website.

The “simbrief.apiv1.php” file will need to be included (<?php include 'simbrief.apiv1.php'; ?>) in your output page. It should reside in the same directory as the rest of your dispatch system, however if you absolutely need to have it in a different directory, you will need to modify the “var api_dir =” line in the “simbrief.apiv1.js” file to point to the proper location.

Prior to using the API, you must obtain an API key and paste it into your “simbrief.apiv1.php” file. Failure to do this will result in your flightplan requests being denied. API keys can be requested by contacting us. Please include information about your website and how you intend on using the API.

API Parameters
The <input> names for each dispatch option are as follows:

Flight Info
Parameter	Input Name	Example Value 1, Example Value 2
Airline	airline	ABC
Flight Number	fltnum	1234
Origin	orig	KORD
Destination	dest	KSFO
Alternate	altn	KLAX
Date	date	11JUL13
Departure Time (Hour)	deph	16
Departure Time (Minute)	depm	30
Static ID**	static_id	ABC_12345
Aircraft Info
Parameter	Input Name	Example Value 1, Example Value 2
Aircraft	type	B738
Climb Profile	climb	250/300/78
Descent Profile	descent	84/280/250
Cruise Profile	cruise	LRC, CI
Cost Index	civalue	25, AUTO
Fuel Factor	fuelfactor	P00
ATC Callsign	callsign	ABC1234
Aircraft Registration	reg	N123XX
Aircraft Fin Number	fin	123
Aircraft SELCAL	selcal	ABCD
Mode-S Code	hexcode	A1B2C3
ICAO Equipment	equipment	SDE3FGHIRWY
Transponder	transponder	LB1
PBN Capability	pbn	A1B1C1D1O1S1
Extra FPL Info (Item 18)	extrarmk	DAT/V RMK/SIMBRIEF
Aircraft Data*	acdata	{“cat”:“M”,“equip”:“SDE3FGHIRWY”,
“transponder”:“S”,“pbn”:“PBN/A1B1C1D1”,
“extrarmk”:“DAT/V RVR/250 RMK/EXAMPLE”,
“maxpax”:“146”,“oew”:96.5,“mzfw”:134.5,
“mtow”:169.8,“mlw”:142.2,“maxfuel”:42.6}
Selections
Parameter	Input Name	Example Value 1, Example Value 2
Plan Format	planformat	LIDO
Units	units	LBS, KGS
Flight Maps	maps	detail, simple, or none
Taxi Out (Minutes)	taxiout	10
Taxi In (Minutes)	taxiin	4
Flight Rules	flightrules	i, v
Flight Type	flighttype	s, x
Detailed Navlog	navlog	1 or 0 (1 enables, 0 disables)
ETOPS Planning	etops	1 or 0 (1 enables, 0 disables)
Plan Stepclimbs	stepclimbs	1 or 0 (1 enables, 0 disables)
Runway Analysis	tlr	1 or 0 (1 enables, 0 disables)
Include NOTAMs	notams	1 or 0 (1 enables, 0 disables)
FIR NOTAMs	firnot	1 or 0 (1 enables, 0 disables)
Optional Entries
Parameter	Input Name	Example Value 1, Example Value 2
Scheduled Time (Hour)	steh	4
Scheduled Time (Minute)	stem	30
Departure Runway	origrwy	06L
Arrival Runway	destrwy	36R
Altitude	fl	34000, FL340
Passengers	pax	100
Freight Added	cargo	5.0
Manual Payload	manualpayload	15.0
Manual ZFW	manualzfw	40.1
Fuel Planning
Parameter	Input Name	Example Value 1, Example Value 2
Cont Fuel (% or minutes)	contpct	0.05, 0.05/15
Reserve Fuel (Minutes)	resvrule	45
Taxi Fuel	taxifuel	0.5
Block Fuel	minfob	50.0
Arrival Fuel	minfod	20.0
MEL Fuel	melfuel	0.5, 20
ATC Fuel	atcfuel	0.5, 20
WXX Fuel	wxxfuel	0.5, 20
Extra Fuel	addedfuel	0.5, 20
Tankering	tankering	2.0, 60
Block Fuel Units	minfob_units	wgt, min
Arrival Fuel Units	minfod_units	wgt, min
MEL Fuel Units	melfuel_units	wgt, min
ATC Fuel Units	atcfuel_units	wgt, min
WXX Fuel Units	wxxfuel_units	wgt, min
Extra Fuel Units	addedfuel_units	wgt, min
Tankering Units	tankering_units	wgt, min
Extra Fuel Label	addedfuel_label	opn, hold
Text Entries
Parameter	Input Name	Example Value 1, Example Value 2
Captain’s Name	cpt	JOHN DOE
Pilot ID Number	pid	12345
First Officer Name	fo	JOHNNY DOE
Dispatcher’s Name	dxname	JANE DOE
Custom Remarks	manualrmk	REMARK TEXT, FIRST REMARK\nSECOND REMARK
Route Options
Parameter	Input Name	Example Value 1, Example Value 2
Route	route	PLL GAROT OAL MOD4
Disable SIDs	omit_sids	1 or 0 (1 omits them, 0 enables them)
Disable STARs	omit_stars	1 or 0 (1 omits them, 0 enables them)
Auto-Insert SID/STARs	find_sidstar	R or C (Prefer RNAV or Non-RNAV)
Alternate Airports
Parameter	Input Name	Example Value 1, Example Value 2
Number of Alternates	altn_count	4
Avoid Alternate Airports	altn_avoid	KJFK KPHL KBWI
Alternate # 1	altn_1_id	KJFK
Alternate # 1 Runway	altn_1_rwy	22R
Alternate # 1 Routing	altn_1_route	SSOXS5 SSOXS DCT SEY PARCH2
Alternate # 2-4 Ident	altn_#_id	KJFK
Alternate # 2-4 Runway	altn_#_rwy	22R
Alternate # 2-4 Routing	altn_#_route	SSOXS5 SSOXS SEY PARCH2
Takeoff Alternate	toaltn	KBOS, AUTO
Takeoff Alternate Distance	toaltn_radius	400
Enroute Alternate	eualtn	KORD, AUTO
ETOPS Scenario
Parameter	Input Name	Example Value 1, Example Value 2
ETOPS Threshold	etopsthreshold	60, 180
ETOPS Rule	etopsrule	180, 207
ETOPS Entry Airport	etopsentry	CYYT
ETOPS Exit Airport	etopsexit	EINN
ETOPS Alternate # 1	etopsaltn1	CYQX
ETOPS Alternate # 2-6	etopsaltn#	EIDW
Note regarding Static ID
This option allows you to set a reference static ID string that will remain the same, even if your user subsequently edits/regenerates their flight plan through the SimBrief website. The Static ID can be any combination of letters, numbers, and the underscore ( _ ) character.

When specified, the Static ID can be used to subsequently pull up the options or data for this API flight, even if the user has since edited the flight or generated a new flight plan on the SimBrief website.

To redirect a user to the SimBrief Options page for this API flight, the following link can be used:

https://www.simbrief.com/system/dispatch.php?editflight=last&static_id={your_static_id}
To pull up the latest XML data for this flight through the XML fetcher script, use the following link:

https://www.simbrief.com/api/xml.fetcher.php?userid={user_id}&static_id={your_static_id}
Note regarding Aircraft Data
Specific airframe data, such as aircraft weight limits and onboard equipment, can now be specified using the “acdata” parameter. This data must be passed as a JSON object, for example:

{"cat":"M","equip":"SDE3FGHIRWY","transponder":"S","pbn":"PBN\/A1B1C1D1",
"extrarmk":"DAT\/V RVR\/250 RMK\/EXAMPLE","maxpax":"146","oew":96.5,"mzfw":134.5,
"mtow":169.8,"mlw":142.2,"maxfuel":42.6,"hexcode":"123ABC","per":"D","paxwgt":175,"bagwgt":55}
The following parameters can be specified:

Airframe Option	Parameter Name	Example Value
ICAO Code	icao	A320
Aircraft Name	name	A320-200
Engine Type	engines	CFM56-5B4
Aircraft Weight Category	cat	L, M, H, or J
Equipment String	equip	SDE3FGHIRWY
Transponder String	transponder	LB1
Performance Based Navigation String	pbn	PBN/A1B1C1D1
Additional FPL Section 18 Info	extrarmk	DAT/V RVR/250 RMK/EXAMPLE
Maximum Passengers	maxpax	146
Operating Empty Weight	oew	96.5
Maximum Zero Fuel Weight	mzfw	134.5
Maximum Takeoff Weight	mtow	169.8
Maximum Landing Weight	mlw	142.2
Maximum Fuel Capacity	maxfuel	42.6
Maximum Cargo Weight	maxcargo	24.4
ICAO Mode-S Code	hexcode	123ABC
ICAO performance category	per	A, B, C, D, or E
Average Passenger Weight	paxwgt	175
Average Baggage Weight	bagwgt	55
Service Ceiling	ceiling	39000
Cruise Level Offset	cruiseoffset	P2000, M0500
Contrary to other dispatch parameters, all weights inside the “acdata” object (except the paxwgt and bagwgt values) must be in thousands of pounds, but can be specified with up to 3 decimal places (in order to set precise values). Also note that for the “cat”, “equip”, and “transponder” options to work, all 3 must be specified. If one of the 3 parameters is missing, all 3 will be ignored.

Please ensure that your JSON string is properly encoded to be passed in a URL, otherwise it may not work correctly.

Approximating Unsupported Aircraft Types
Unsupported aircraft types can be approximated, or “faked”, by adding additional parameters to the “acdata” JSON string explained above. Simply add any or all of these 3 additional parameters to the JSON string to simulate a different aircraft type: “icao” (the ICAO aircraft identifier, max 4 characters), “name” (the aircraft name, max 12 characters), and “engines” (the aircraft engine type, max 12 characters).

For example, a JSON string which would simulate a fictional Airbus A322 might look like this:

{"icao":"A322","name":"A322-200","engines":"CFM56ZZZ\/B","cat":"M","equip":"SDE3FGHIRWY",
"transponder":"S","pbn":"PBN\/A1B1C1D1","maxpax":"205","oew":106.5,"mzfw":154.5,"mtow":199.8,
"mlw":182.2,"maxfuel":52.6}
Note that limited support is given for this feature. Please try to base your faked aircraft on an aircraft which has similar weight/characteristics. Basing a faked aircraft on an aircraft which is much lighter/heavier can cause weird fuel figures as the system will have to extrapolate well outside the weights for which the original aircraft was programmed.

Using custom user airframes with the API
It’s now possible to use a custom airframe that a user has saved to their SimBrief profile in an API request. To do so, simply have the user open the airframe for editing, and note the “Internal ID” number which appears near the top of the airframe options (it should look something like “123456_1582090020”). Then, simply send this ID in your API request instead of the aircraft type code (for example, instead of sending “B738”, you would send “123456_1582090020”).

When using this method, please note the following: Any changes that the airframe owner subsequently makes to this airframe will also immediately be reflected in any subsequent API flights you generate. Also, should the owner delete the airframe from their profile, any future API requests you make using this airframe’s Internal ID will fail with an “Unknown Aircraft” error.

View the Demo
A demo of the API can be viewed here. Please pay no attention to the page style (or lack thereof), it is merely meant to illustrate how the popup window works and what kind of information is available afterwards in the resulting XML file. It should be noted that the PHP class can also provide the data as a standard PHP array and as a JSON object.