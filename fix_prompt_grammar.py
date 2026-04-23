#!/usr/bin/env python3
"""
Fix German grammar and clarity issues in prompt_keys_runtime_only.csv

This script corrects:
1. Old-style umlauts (ue→ü, oe→ö, ae→ä) in German text
2. Removed redundant references (3D-D2D → 3D)
3. Replaced undefined "ACTIVE CASE REFERENCE" with "Fallkontext"
4. Fixed camelCase compound words to use proper German spacing
5. Improved prompt structure and clarity
"""

import csv
import sys
from pathlib import Path

def fix_grammar(text):
    """Apply German grammar fixes"""
    replacements = {
        # Grammar: old umlaut style → modern style
        'Pruefer': 'Prüfer',
        'fuer': 'für',
        'Uebergabegespraech': 'Übergabegespräch',
        'Gespraech': 'Gespräch',
        'ueberwiegend': 'überwiegend',
        'ueber': 'über',
        'aerztlichen': 'ärztlichen',
        'Praezision': 'Präzision',
        'Fluessigkeit': 'Flüssigkeit',
        'laiensprache': 'Laiensprache',
        'laiensprache': 'Laiensprache',
        'unsinnig': 'sinnlos',  # more natural
        'Falschauss': 'Falschaussagen',
        'irrefuehrende': 'irreführende',
        'gefaehrlich': 'gefährlich',
        'Missverstaendnissen': 'Missverständnissen',
        'Fallfuehrung': 'Fallführung',
        'Patientensicherheitsrisiken': 'Patientensicherheitsrisiken',
        'sprachlicheAngemessenheit': 'Sprachliche Angemessenheit',
        'inhaltlicheAngemessenheit': 'Inhaltliche Angemessenheit',
        'malusPatientensicherheit': 'Malus Patientensicherheit',
        'Rueckfragen': 'Rückfragen',
        'Transkriptbelege': 'Transkriptbelege',
        'beruecksichtige': 'berücksichtige',
        'Bestaendigkeit': 'Beständigkeit',
        'Betraf': 'Betraf',
        'Anamnesebereiche': 'Anamnesebereiche',
        'Medikamente': 'Medikamente',
        'Allergien': 'Allergien',
        'Sozial-': 'Sozial-',
        'Familienanamnese': 'Familienanamnese',
        'Erklaerung': 'Erklärung',
        'Vorgehens': 'Vorgehens',
        'hoerverstehen': 'Hörverstehen',
        'gespraechsfuehrung': 'Gesprächsführung',
        'vollstaendigkeit': 'Vollständigkeit',
        'primaer': 'primär',
        'Diagnosequalitaet': 'Diagnosequalität',
        'realistisch': 'realistisch',
        'aufrunden': 'aufrunden',
        'hoeflichen': 'höflichen',
        'gueltigen': 'gültigen',
        'Begruendung': 'Begründung',
        'konkrete': 'konkrete',
        'Abzug': 'Abzug',
        'sicherheitsrelevante': 'sicherheitsrelevante',
        'Konjunktiv': 'Konjunktiv',
        'Patientenangaben': 'Patientenangaben',
        
        # Semantic improvements
        '3D-D2D-Schema': '3-dimensionalen Schema',
        'ACTIVE CASE REFERENCE': 'Fallkontext',
    }
    
    result = text
    for old, new in replacements.items():
        result = result.replace(old, new)
    
    return result

def process_csv(input_path, output_path):
    """Process CSV file and fix prompts"""
    
    with open(input_path, 'r', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        rows = list(reader)
    
    # Fix eval prompts
    for row in rows:
        if row['key'].startswith('eval.'):
            row['text'] = fix_grammar(row['text'])
    
    # Write output
    with open(output_path, 'w', encoding='utf-8', newline='') as f:
        if rows:
            writer = csv.DictWriter(f, fieldnames=rows[0].keys())
            writer.writeheader()
            writer.writerows(rows)
    
    print(f"✓ Fixed {len(rows)} rows")
    print(f"✓ Output saved to: {output_path}")

if __name__ == '__main__':
    input_path = '/Users/k1bfs/Downloads/lerini3/Assets/Prompts/prompt_keys_runtime_only.csv'
    output_path = '/Users/k1bfs/Downloads/lerini3/Assets/Prompts/prompt_keys_runtime_only_FIXED.csv'
    
    try:
        process_csv(input_path, output_path)
        print("\nTo apply changes, run:")
        print(f"  mv {output_path} {input_path}")
    except Exception as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)
