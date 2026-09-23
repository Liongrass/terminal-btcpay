package main

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"

	"gopkg.in/macaroon.v2"
)

type vector struct {
	Note    string   `json:"note"`
	RootKey string   `json:"rootKeyHex"`
	Id      string   `json:"idHex"`
	Loc     string   `json:"location"`
	Before  string   `json:"beforeHex"`
	Caveats []string `json:"caveats"`
	After   string   `json:"afterHex"`
}

func build(note, rootKeyHex, idHex, loc string, caveats []string) vector {
	rootKey, _ := hex.DecodeString(rootKeyHex)
	id, _ := hex.DecodeString(idHex)
	m, err := macaroon.New(rootKey, id, loc, macaroon.V2)
	if err != nil {
		panic(err)
	}
	before, _ := m.MarshalBinary()
	for _, c := range caveats {
		if err := m.AddFirstPartyCaveat([]byte(c)); err != nil {
			panic(err)
		}
	}
	after, _ := m.MarshalBinary()
	return vector{note, rootKeyHex, idHex, loc, hex.EncodeToString(before), caveats, hex.EncodeToString(after)}
}

func main() {
	vs := []vector{
		build("the real shape: an account caveat on an lnd-style macaroon",
			"4c9e2f1b8a7d6c5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e",
			"0201036c6e6402f801030a10a4b2c3d4e5f60718293a4b5c6d7e8f00",
			"lnd", []string{"lnd-custom account 0011223344556677"}),
		build("no caveats at all", "00112233445566778899aabbccddeeff", "abcd", "lnd", nil),
		build("two caveats chain their signatures", "ff", "00", "",
			[]string{"lnd-custom account 0011223344556677", "time-before 2026-09-30T00:00:00Z"}),
		build("empty location, empty id", "0f0e0d0c", "", "", []string{"lnd-custom account aabbccddeeff0011"}),
		build("utf8 and a long caveat", "0102030405060708", "6c6e64",
			"a-rather-long-location-name.example",
			[]string{"lnd-custom account 00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"}),
	}
	out, _ := json.MarshalIndent(vs, "", "  ")
	os.WriteFile("vectors.json", out, 0644)
	fmt.Printf("wrote %d vectors\n", len(vs))
}
